"""Independently reconcile archived study cells and compute block-level statistics."""

import argparse
from collections import Counter, defaultdict
from datetime import datetime
from decimal import Decimal
import gzip
import hashlib
import json
import math
from pathlib import Path, PurePosixPath
import statistics

GROUPS = "ABCD"
SCENARIOS = ("W1-low", "W1-high", "W2", "W4")
OUTCOMES = ("correctlyConfirmed", "duplicate", "clientMismatch", "unreconciled", "late", "unknown",
            "businessRejected", "operationalFailure")
BUSINESS = {"accountUnavailable", "insufficientFunds", "invalidRequest", "invalidKey", "keyPayloadMismatch"}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def read_json(path):
    return json.loads(path.read_bytes())


def evidence_path(root, relative):
    relative_path = PurePosixPath(relative)
    target = (root / relative).resolve()
    require(not relative_path.is_absolute() and "\\" not in relative and ":" not in relative
            and ".." not in relative_path.parts and target.is_relative_to(root), "Unsafe evidence member path")
    return target


def schedule_key(cell):
    return cell["block"], cell["scenario"], cell["group"], cell["seed"]


class Dataset:
    def __init__(self, root, phase, freeze_path):
        self.root = root.resolve()
        self.path = self.root / "study-checkpoint.json"
        self.checkpoint = read_json(self.path)
        self.sha256 = digest(self.path)
        self.binding = self.checkpoint["binding"]
        self.plan = self.binding["plan"]
        self.freeze = read_json(freeze_path)
        self.phase = phase
        self.cells = defaultdict(list)
        self.identities = set()
        self.eligible = {}
        require(self.checkpoint["complete"] and self.plan["phase"] == phase, f"Incomplete or mislabeled {phase}")
        require(len(self.plan["cells"]) == len(SCENARIOS) * len(GROUPS) * self.plan["blocks"],
                f"Unexpected {phase} plan size")
        require(self.plan["settings"]["measureSeconds"] == 60, "The registered measurement window differs")
        for cell in self.plan["cells"]:
            self.cells[cell["block"]].append(cell)
        self.seeds = set()
        for block, cells in self.cells.items():
            self.seeds.add(cells[0]["seed"])
            require({(c["scenario"], c["group"]) for c in cells} == {(s, g) for s in SCENARIOS for g in GROUPS}
                    and len({c["seed"] for c in cells}) == 1 and block > 0, "Incoherent complete block schedule")
        require(sorted(self.cells) == list(range(1, self.plan["blocks"] + 1))
                and len(self.seeds) == len(self.cells), "Wrong block IDs or repeated workload seeds")

        if phase == "formal":
            require(self.binding["freezeSha256"] == digest(freeze_path) and self.plan == self.freeze["plan"],
                    "Formal checkpoint differs from the frozen protocol")
            keys = ("sourceSha256", "buildSha256", "runtime", "sourceFiles", "buildFiles")
            require(all(self.binding["identity"][k] == self.freeze["identity"][k] for k in keys)
                    and self.binding["scopeSha256"] == self.freeze["scopeSha256"]
                    and self.binding["settingsSha256"] == self.freeze["settingsSha256"]
                    and self.freeze["codeCommit"] == self.freeze["identity"]["observedHead"],
                    "Formal code, binary, runtime or scope identity differs")
        else:
            require(self.binding["freezeSha256"] is None and self.sha256 == self.freeze["pilotCheckpointSha256"],
                    "Pilot identity or separation differs")
        self.members = {self.path}
        self.index = []
        self.read_index()

    def indexed_file(self, row):
        path = evidence_path(self.root, row["file"])
        require(path not in self.members and digest(path) == row["sha256"],
                f"Duplicate or changed member {row['file']}")
        self.members.add(path)
        return path

    def read_index(self):
        attempts = self.checkpoint["attempts"]
        next_attempt = Counter()
        complete = 0
        invalid = 0
        for attempt in attempts:
            block = attempt["block"]
            next_attempt[block] += 1
            require(block == complete + 1 and attempt["attempt"] == next_attempt[block],
                    "Checkpoint attempts are out of order")
            scheduled = self.cells[block]
            valid = attempt["state"] == "complete"
            if valid:
                complete += 1
            else:
                require(attempt["state"] == "invalid" and attempt["reason"], "Unreleased safety or missing invalid reason")
                invalid += 1
            runs = attempt["runs"]
            require(attempt["activeSlot"] is None and len(runs) <= len(scheduled)
                    and (not valid or len(runs) == len(scheduled)), "Missing or unfinalised cell in an indexed attempt")
            for i, row in enumerate(runs):
                require(row["summary"]["cell"] == scheduled[i], "Index differs from the randomized schedule")
                require(row["summary"]["runId"] not in self.identities, "Run ID reused")
                self.identities.add(row["summary"]["runId"])
                path = self.indexed_file(row)
                self.index.append((scheduled[i], row, path, valid))
                if valid:
                    self.eligible[schedule_key(scheduled[i])] = (row, path)
            for row in attempt["failureEvidence"]:
                self.indexed_file(row)
        require(complete == self.plan["blocks"] == self.checkpoint["completedBlocks"]
                and invalid == self.checkpoint["invalidBlocks"]
                and len(attempts) - len(self.cells) == self.checkpoint["reruns"], "Checkpoint counts differ from its index")
        require(set(p.resolve() for p in self.root.rglob("*") if p.is_file()) == self.members,
                "Dataset contains unindexed or missing members")
        if self.checkpoint.get("recovery"):
            recovery = self.checkpoint["recovery"]
            for name, field in (("checkpoint-before-recovery.json", "originalCheckpointSha256"),
                                ("checkpoint-unpublished.json", "originalPendingSha256")):
                originals = [p for p in self.members if p.name == name]
                require(len(originals) == 1 and digest(originals[0]) == recovery[field],
                        f"Recovery provenance differs for {name}")

    def read_cell(self, cell, row, path):
        with gzip.open(path, "rt", encoding="utf-8") as stream:
            raw = json.load(stream)
        require(raw["runId"] == row["summary"]["runId"] and raw["cell"] == cell
                and raw["settings"] == self.plan["settings"], f"Identity differs in {path.name}")
        if self.phase == "formal":
            require(datetime.fromisoformat(raw["startedUtc"]) >= datetime.fromisoformat(self.freeze["frozenUtc"]),
                    f"Measurement predates its freeze: {path.name}")
        return raw


def settled_effects(raw, clients):
    ledger = raw["ledger"]
    initial = {r["id"]: Decimal(str(r["balance"])) for r in raw["initial"]}
    final = {r["id"]: Decimal(str(r["balance"])) for r in ledger["accounts"]}
    expected = initial.copy()
    transfers = {}
    by_logical = defaultdict(list)
    postings = Counter()
    sound = len(initial) == len(raw["initial"]) and len(final) == len(ledger["accounts"])
    for row in ledger["transfers"]:
        tid = row["transferId"]
        request = clients.get(row["attemptId"])
        source, target = row["sourceAccount"], row["destinationAccount"]
        amount = Decimal(str(row["amount"]))
        keys = ("logicalRequestId", "sourceAccount", "destinationAccount", "amount", "currency")
        sound &= (request is not None and all(request[k] == row[k] for k in keys)
                  and tid not in transfers and source != target and amount > 0)
        transfers[tid] = row
        by_logical[row["logicalRequestId"]].append(row)
        postings[tid, source, "debit", -amount] += 1
        postings[tid, target, "credit", amount] += 1
        expected[source] = expected.get(source, 0) - amount
        expected[target] = expected.get(target, 0) + amount
    actual = Counter((r["transferId"], r["accountId"], r["entryType"], Decimal(str(r["signedAmount"])))
                     for r in ledger["entries"])
    sound &= (actual == postings and expected == final and initial.keys() == final.keys()
              and sum(initial.values()) == sum(final.values()) and all(v >= 0 for v in final.values()))
    return bool(sound), transfers, by_logical


def classify(row, effects, transfer_by_id, cutoff_ms, sound):
    attempts = row["attempts"]
    if not attempts:
        return row["miss"], None, 0, 0
    logical_id = row["input"]["request"]["logicalRequestId"]
    acknowledgements = []
    mismatches = 0
    statuses = set()
    for attempt in attempts:
        reply = attempt["reply"] or {}
        statuses.add(reply.get("status"))
        if reply.get("status") == "committed":
            transfer = transfer_by_id.get(reply.get("transferId"), {})
            if attempt["httpStatus"] == 200 and transfer.get("logicalRequestId") == logical_id:
                acknowledgements.append(attempt["endedMs"])
            else:
                mismatches += 1
    received = min(acknowledgements, default=math.inf)
    late = int(bool(acknowledgements) and received > cutoff_ms)
    if len(effects) > 1:
        label = "duplicate"
    elif mismatches:
        label = "clientMismatch"
    elif len(effects) == 1 and received <= cutoff_ms:
        label = "correctlyConfirmed" if sound else "unreconciled"
    elif late:
        label = "late"
    elif effects or "unknownCommit" in statuses or any(a["transportFailure"] for a in attempts):
        label = "unknown"
    elif BUSINESS & statuses:
        label = "businessRejected"
    else:
        label = "operationalFailure"
    latency = received - attempts[0]["sentMs"] if label == "correctlyConfirmed" else None
    return label, latency, mismatches, late


def nearest_rank(values, percent):
    return sorted(values)[math.ceil(len(values) * percent / 100) - 1] if values else None


def cell_evidence(raw, keep_requests=False):
    window = raw["measurement"]
    rows = window["logicalRequests"]
    rule = raw["settings"]
    seconds = rule["measureSeconds"]
    require(len(rows) == seconds * rule["offeredPerSecond"] and seconds == 60
            and window["cutoffMs"] == (seconds + rule["drainSeconds"]) * 1000, "Incorrect exposure window")
    require([r["input"]["order"] for r in rows] == list(range(len(rows))), "Offered schedule is not a complete sequence")
    client = {}
    for row in rows:
        attempts = row["attempts"]
        if not attempts:
            require(row["miss"] in ("capacity", "window"), "Missing dispatch reason")
            continue
        request = row["input"]["request"]
        require(1 <= len(attempts) <= 2 and not row["miss"], "Impossible dispatch record")
        require(attempts[0]["request"] == request and not attempts[0]["repeat"]
                and 0 <= attempts[0]["sentMs"] < seconds * 1000, "Wrong first dispatch cohort or payload")
        if len(attempts) == 2:
            require(attempts[1]["repeat"] and row["input"]["repeatAssigned"]
                    and attempts[1]["request"] == {**request, "attemptId": row["input"]["repeatAttemptId"]},
                    "Repeat differs from the offered request")
        for attempt in attempts:
            sent = attempt["request"]
            require(sent["attemptId"] not in client, "Duplicate client attempt ID")
            client[sent["attemptId"]] = sent

    trace = {}
    for row in window["serverAttempts"]:
        request = row["attempt"]["request"]
        uid = request["attemptId"]
        require(uid not in trace and client.get(uid) == request, "Server admission cannot be joined to the client trace")
        trace[uid] = row
    sound, transfers, by_logical = settled_effects(raw, client)
    counts = Counter()
    exposure = Counter()
    outcomes = Counter(dict.fromkeys(OUTCOMES, 0))
    latencies, lags = [], []
    seen = set()
    matched = {}
    signature = hashlib.sha256()
    for row in rows:
        offered = row["input"]
        uid = offered["request"]["logicalRequestId"]
        require(uid not in seen, "Logical ID reused inside a cell")
        seen.add(uid)
        signature.update(json.dumps((offered, row["scheduledMs"]), sort_keys=True, separators=(",", ":")).encode())
        batch = by_logical.get(uid, [])
        label, latency, mismatches, late = classify(row, batch, transfers, window["cutoffMs"], sound)
        attempts = row["attempts"]
        counts["extraFinancialEffects"] += max(0, len(batch) - 1)
        counts["clientLedgerMismatch"] += mismatches
        counts["lateConfirmations"] += late
        exposure["assigned"] += int(offered["repeatAssigned"])
        if attempts:
            outcomes[label] += 1
            lags.append(attempts[0]["sentMs"] - row["scheduledMs"])
            if latency is not None:
                latencies.append(latency)
            counts["dispatched"] += 1
            counts["oneEffectLogical"] += len(batch) == 1
            counts["zeroEffectLogical"] += not batch
            evidence_exposure(raw["cell"]["scenario"], offered["repeatAssigned"], attempts, trace, exposure)
        else:
            counts["capacityMisses" if label == "capacity" else "windowMisses"] += 1
        if keep_requests:
            matched[uid] = (label, row, batch)

    server_results = [r["attempt"]["result"] for r in trace.values()]
    resources = window["resources"]
    counts.update({
        "offered": len(rows), "httpAttempts": len(client), "committedEffects": len(transfers),
        "duplicateLogical": outcomes["duplicate"],
        "transactionAttempts": sum(x["transactionAttempts"] for x in server_results),
        "internalRetries": sum(max(0, x["transactionAttempts"] - 1) for x in server_results),
        "versionConflicts": sum(x["versionConflicts"] for x in server_results),
        "lockTimeouts": sum(x["lockTimeouts"] for x in server_results),
        "retryExhausted": sum(x["status"] == "retryExhausted" for x in server_results),
        "replays": sum(x["replayed"] for x in server_results),
        "keyBusy": sum(x["status"] == "keyBusy" for x in server_results),
        "resourceSamples": len(resources), "lockWaiterSamples": sum(x["lockWaiters"] > 0 for x in resources),
    })
    counts.setdefault("capacityMisses", 0)
    counts.setdefault("windowMisses", 0)
    measures = {"correctlyConfirmedTps": len(latencies) / seconds, "httpAttemptTps": len(client) / seconds,
                "p95DispatchLagMs": nearest_rank(lags, 95),
                **{f"p{p}Ms": nearest_rank(latencies, p) for p in (50, 95, 99)}}
    return {"counts": dict(counts), "outcomes": dict(outcomes), "measures": measures,
            "exposure": {k: exposure[k] for k in ("assigned", "assignedDispatched", "eligible", "triggered",
                                                   "repeated", "serverOverlap", "injectedTransportLosses",
                                                   "otherTransportFailures")},
            "ledgerReconciled": sound, "inputSignature": signature.hexdigest(), "reset": raw["verifiedResetSha256"],
            "requests": matched, "trace": trace if keep_requests else {}}


def evidence_exposure(scenario, assigned, attempts, trace, counts):
    first = attempts[0]
    first_seen = trace.get(first["request"]["attemptId"])
    counts["otherTransportFailures"] += sum(bool(a["transportFailure"]) for a in attempts)
    counts["repeated"] += sum(bool(a["repeat"]) for a in attempts)
    if not assigned:
        return
    counts["assignedDispatched"] += 1
    if scenario == "W2":
        counts["eligible"] += 1
        if len(attempts) == 2:
            second = attempts[1]
            second_seen = trace.get(second["request"]["attemptId"])
            if first_seen and second_seen:
                counts["triggered"] += int(second["sentMs"] <= first["endedMs"])
                boundaries = (first_seen["admittedMs"], second_seen["admittedMs"])
                if None not in boundaries:
                    counts["serverOverlap"] += max(boundaries) <= min(first_seen["observedMs"], second_seen["observedMs"])
    elif scenario == "W4" and first_seen:
        server = first_seen["attempt"]
        if server["result"]["status"] == "committed" and not server["result"]["replayed"]:
            counts["eligible"] += 1
            if server["responseDropped"]:
                counts["triggered"] += 1
                if first["transportFailure"] == "http-transport":
                    counts["injectedTransportLosses"] += 1
                    counts["otherTransportFailures"] -= 1
    if scenario == "W4" and len(attempts) == 2:
        require(first_seen and first_seen["attempt"]["responseDropped"] and first["transportFailure"] == "http-transport",
                "A W4 repeat has no attributed response loss")


def same_number(a, b):
    return (a == b) if (a is None or b is None) else math.isclose(a, b, rel_tol=0.0, abs_tol=1e-6)


def verify_cell(got, saved, name):
    require(got["outcomes"] == saved["outcomes"] and got["exposure"] == saved["exposure"]
            and got["ledgerReconciled"] == (not saved["financialIssues"])
            and all(v == saved[k] for k, v in got["counts"].items())
            and all(same_number(v, saved[k]) for k, v in got["measures"].items()),
            f"Independent outcomes, cost, quantiles or exposure differ in {name}")


def audit(dataset):
    cells = {}
    signatures = defaultdict(set)
    resets = defaultdict(set)
    aggregate = Counter()
    for cell, evidence, path, valid in dataset.index:
        raw = dataset.read_cell(cell, evidence, path)
        got = cell_evidence(raw)
        verify_cell(got, evidence["summary"], path.name)
        if not valid:
            continue
        block, scenario, group, seed = schedule_key(cell)
        signatures[block, scenario].add(got["inputSignature"])
        resets[block].add(got["reset"])
        cells[block, scenario, group] = got
        aggregate.update(got["counts"])
        aggregate.update(got["outcomes"])
        aggregate["financialInvariantViolationRuns"] += not got["ledgerReconciled"]
    for block in dataset.cells:
        require(all(len(signatures[block, s]) == 1 for s in SCENARIOS)
                and signatures[block, "W2"] == signatures[block, "W4"] and len(resets[block]) == 1,
                f"Different seed workloads or resets in block {block}")
    return cells, dict(aggregate)


def factorial(a, b, c, d):
    return {"occ": (b + d - a - c) / 2, "idempotency": (c + d - a - b) / 2, "interaction": d - c - b + a}


def interval(points):
    from scipy.stats import t
    n = len(points)
    mean = statistics.mean(points) if n else None
    sd = statistics.stdev(points) if n > 1 else None
    half = float(t.ppf(0.975, n - 1)) * sd / math.sqrt(n) if sd is not None else None
    return {"n": n, "mean": mean, "median": statistics.median(points) if n else None, "sd": sd,
            "lower": mean - half if half is not None else None, "upper": mean + half if half is not None else None,
            "halfWidth": half,
            "standardisedPairedEffect": mean / sd if sd else None}


def effects(cells, n):
    result = {}
    for scenario in SCENARIOS:
        result[scenario] = {}
        for metric in ("correctlyConfirmedTps", "p95Ms"):
            blocks = [tuple(cells[b, scenario, g]["measures"][metric] for g in GROUPS) for b in range(1, n + 1)]
            computed = {i: factorial(*row) for i, row in enumerate(blocks, 1) if None not in row}
            result[scenario][metric] = {}
            for name in ("occ", "idempotency", "interaction"):
                points = [{"block": b, "value": v[name]} for b, v in computed.items()]
                result[scenario][metric][name] = {"points": points, "interval": interval([x["value"] for x in points])}
    return result


def check_effects(reference, observed):
    for scenario, measures in observed.items():
        for metric, names in measures.items():
            for name, result in names.items():
                published = reference["factorial"][scenario][metric][name]
                require([v["block"] for v in published["points"]] == [v["block"] for v in result["points"]]
                        and all(same_number(x["value"], y["value"])
                                for x, y in zip(published["points"], result["points"], strict=True))
                        and all(same_number(value, published["interval"][key])
                                for key, value in result["interval"].items()),
                        f"Published factorial estimate differs for {scenario}/{metric}/{name}")


def contrast(cells, n):
    points = []
    for block in range(1, n + 1):
        def interaction(scenario):
            a, b, c, d = (cells[block, scenario, g]["outcomes"]["correctlyConfirmed"] for g in GROUPS)
            return d - c - b + a
        points.append((interaction("W4") - interaction("W2")) / 60)
    return points


def run(pilot_dir, formal_dir, freeze, reference):
    pilot = Dataset(pilot_dir, "pilot", freeze)
    formal = Dataset(formal_dir, "formal", freeze)
    require(not (pilot.identities & formal.identities or pilot.seeds & formal.seeds)
            and pilot.plan["settings"] == formal.plan["settings"], "Pilot sample or settings separation differs")
    pc, ptotal = audit(pilot)
    fc, ftotal = audit(formal)
    n = len(formal.cells)
    factorial_data = effects(fc, n)
    published = read_json(reference)
    require(formal.sha256 == published["manifestSha256"], "Published analysis references a different manifest")
    check_effects(published, factorial_data)
    points = contrast(fc, n)
    rq3 = interval(points)
    require(all(same_number(a, b["value"]) for a, b in zip(points, published["rq3"]["points"], strict=True))
            and all(same_number(value, published["rq3"]["interval"][k]) for k, value in rq3.items()),
            "Independent RQ3 estimate differs")
    ref = statistics.mean(pc[b, "W1-high", "A"]["measures"]["correctlyConfirmedTps"] for b in pilot.cells)
    target = ref / 10
    pilot_sd = statistics.stdev(contrast(pc, len(pilot.cells)))
    require(same_number(target, formal.freeze["precision"]["targetHalfWidth"])
            and same_number(pilot_sd, formal.freeze["precision"]["sampleStandardDeviation"])
            and same_number(target, published["rq3"]["targetHalfWidth"]), "Pilot reference or frozen precision differs")
    classification = ("confirmatory" if formal.freeze["precision"]["status"] == "confirmatory-candidate"
                      and rq3["halfWidth"] <= target else "exploratory")
    require(classification == published["rq3"]["classification"], "RQ3 classification differs")
    def record(data, total):
        return {"checkpointSha256": data.sha256, "indexedRuns": len(data.index), "eligibleRuns": len(data.eligible),
                "blocks": len(data.cells), "invalid": data.checkpoint["invalidBlocks"], "reruns": data.checkpoint["reruns"],
                "totals": total}
    study = {"pilot": record(pilot, ptotal), "formal": record(formal, ftotal),
             "pilotSD": pilot_sd, "targetHalfWidth": target, "rq3Points": points, "rq3": rq3,
             "classification": classification, "factorial": factorial_data,
             "codeCommit": formal.freeze["codeCommit"], "freezeSha256": digest(freeze)}
    study["formalCells"] = [{"block": b, "scenario": s, "group": g, **{
        k: v[k] for k in ("counts", "outcomes", "exposure", "measures")}} for (b, s, g), v in sorted(fc.items())]
    return study


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for argument in ("pilot", "formal", "freeze", "published_analysis", "output"):
        parser.add_argument(argument, type=Path)
    args = parser.parse_args()
    study = run(args.pilot, args.formal, args.freeze, args.published_analysis)
    with args.output.open("x", encoding="utf-8", newline="\n") as out:
        json.dump(study, out, ensure_ascii=False, indent=2, allow_nan=False)
        out.write("\n")
    print(json.dumps({k: study[k] for k in ("pilot", "formal", "rq3", "classification")}, ensure_ascii=False))


if __name__ == "__main__":
    main()
