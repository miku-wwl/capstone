"""Audit the archived raw evidence independently and analyse complete paired blocks."""

import argparse
import gzip
import hashlib
import json
import math
import statistics as st
from collections import Counter, defaultdict
from decimal import Decimal
from pathlib import Path

from pack_evidence import digest, relative_file

SCENARIOS = ("W1-low", "W1-high", "W2", "W4")
GROUPS = "ABCD"
T95 = (None, 12.706205, 4.302653, 3.182446, 2.776445, 2.570582,
       2.446912, 2.364624, 2.306004, 2.262157, 2.228139, 2.200985, 2.178813, 2.160369)
BUSINESS = {"accountUnavailable", "insufficientFunds", "keyPayloadMismatch", "invalidKey", "invalidRequest"}


def check(condition, message):
    if not condition:
        raise ValueError(message)


def percentile(values, p):
    return sorted(values)[math.ceil(p * len(values)) - 1] if values else None


def interval(values):
    n = len(values)
    mean = st.mean(values) if n else None
    sd = st.stdev(values) if n > 1 else None
    half = T95[n - 1] * sd / math.sqrt(n) if sd is not None else None
    return {"n": n, "mean": mean, "median": st.median(values) if n else None,
            "sd": sd, "halfWidth": half, "lower": mean - half if half is not None else None,
            "upper": mean + half if half is not None else None,
            "standardisedPairedEffect": mean / sd if sd else None}


def audit_ledger(raw, input_attempts):
    ledger = raw["ledger"]
    before = {x["id"]: Decimal(x["balance"]) for x in raw["initial"]}
    expected = before.copy()
    after = {x["id"]: Decimal(x["balance"]) for x in ledger["accounts"]}
    entries = defaultdict(list)
    for item in ledger["entries"]:
        entries[item["transferId"]].append((item["accountId"], item["entryType"], Decimal(item["signedAmount"])))
        expected[item["accountId"]] = expected.get(item["accountId"], Decimal(0)) + Decimal(item["signedAmount"])
    counts = Counter()
    deposits = {}
    passed = (expected == after and before.keys() == after.keys() and sum(before.values()) == sum(after.values())
              and all(m >= 0 for m in after.values()))
    keys = ("logicalRequestId", "sourceAccount", "destinationAccount", "amount", "currency")
    for transfer in ledger["transfers"]:
        tid = transfer["transferId"]
        amount = Decimal(transfer["amount"])
        pair = [(transfer["sourceAccount"], "debit", -amount), (transfer["destinationAccount"], "credit", amount)]
        request = input_attempts.get(transfer["attemptId"])
        passed &= (request is not None and tuple(request[k] for k in keys) == tuple(transfer[k] for k in keys)
                   and sorted(pair) == sorted(entries[tid]) and tid not in deposits)
        logical_id = transfer["logicalRequestId"]
        counts[logical_id] += 1
        deposits[tid] = logical_id
    passed &= entries.keys() == deposits.keys()
    return bool(passed), counts, deposits


def compare_summary(raw, saved):
    window = raw["measurement"]
    records = window["logicalRequests"]
    settings = raw["settings"]
    dispatched = [row for row in records if row["attempts"]]
    attempts = [attempt for row in dispatched for attempt in row["attempts"]]
    requests = {a["request"]["attemptId"]: a["request"] for a in attempts}
    traces = {t["attempt"]["request"]["attemptId"]: t for t in window["serverAttempts"]}
    check(len(requests) == len(attempts) and len(traces) == len(window["serverAttempts"]),
          "Attempt IDs are not unique")
    check(traces.keys() <= requests.keys(), "Trace has no dispatched input")
    check(all(float(row["attempts"][0]["sentMs"]) < settings["measureSeconds"] * 1000 for row in dispatched),
          "A first dispatch fell outside the measurement window")
    passed, effects, transfer_ids = audit_ledger(raw, requests)
    check(passed == (not saved["financialIssues"]), "Independent ledger finding differs")
    check(raw["runId"] == saved["runId"] and raw["cell"] == saved["cell"], "Run identity differs")
    metrics = dict.fromkeys(("correctlyConfirmed", "duplicate", "clientMismatch", "unreconciled",
                            "late", "unknown", "businessRejected", "operationalFailure"), 0)
    counts = Counter()
    latencies = []
    scenario = raw["cell"]["scenario"]
    hot = max(1, min(settings["accounts"] - 1, int(settings["accounts"] * settings["hotAccountFraction"])))
    for row in dispatched:
        row_attempts = row["attempts"]
        first = row_attempts[0]
        logical_id = first["request"]["logicalRequestId"]
        final_effects = effects[logical_id]
        counts["hot"] += first["request"]["sourceAccount"] <= hot
        counts["extra"] += max(0, final_effects - 1)
        counts["zero"] += final_effects == 0
        counts["one"] += final_effects == 1
        acknowledgements = []
        unmatched = False
        replies = [a.get("reply") or {} for a in row_attempts]
        for a, reply in zip(row_attempts, replies):
            if reply.get("status") != "committed":
                continue
            if a["httpStatus"] != 200 or transfer_ids.get(reply.get("transferId")) != logical_id:
                counts["mismatched_receipts"] += 1
                unmatched = True
            else:
                acknowledgements.append(float(a["endedMs"]))
        ack = min(acknowledgements, default=math.inf)
        counts["inclusive_late"] += bool(acknowledgements) and ack > window["cutoffMs"]
        if final_effects > 1:
            terminal = "duplicate"
        elif unmatched:
            terminal = "clientMismatch"
        elif final_effects == 1 and ack <= window["cutoffMs"]:
            counts["matched_on_time"] += 1
            terminal = "correctlyConfirmed" if passed else "unreconciled"
            if passed:
                latencies.append(ack - float(first["sentMs"]))
        elif acknowledgements:
            terminal = "late"
        elif final_effects == 1 or any(a["transportFailure"] or r.get("status") == "unknownCommit"
                                       for a, r in zip(row_attempts, replies)):
            terminal = "unknown"
        elif any(r.get("status") in BUSINESS for r in replies):
            terminal = "businessRejected"
        else:
            terminal = "operationalFailure"
        metrics[terminal] += 1
        counts["other"] += sum(bool(a["transportFailure"]) for a in row_attempts)
        counts["repeats"] += sum(a["repeat"] for a in row_attempts)
        if not row["input"]["repeatAssigned"]:
            continue
        counts["assigned_dispatched"] += 1
        trace = traces.get(first["request"]["attemptId"])
        if scenario == "W2":
            counts["eligible"] += 1
            if len(row_attempts) == 2:
                repeat = row_attempts[1]
                other_trace = traces.get(repeat["request"]["attemptId"])
                if trace and other_trace:
                    counts["triggered"] += repeat["sentMs"] <= first["endedMs"]
                    if trace.get("admittedMs") is not None and other_trace.get("admittedMs") is not None:
                        counts["overlap"] += max(trace["admittedMs"], other_trace["admittedMs"]) <= min(
                            trace["observedMs"], other_trace["observedMs"])
        elif scenario == "W4" and trace:
            result = trace["attempt"]["result"]
            if result["status"] == "committed" and not result["replayed"]:
                counts["eligible"] += 1
                if trace["attempt"]["responseDropped"]:
                    counts["triggered"] += 1
                    if first["transportFailure"] == "http-transport":
                        counts["injected"] += 1
                        counts["other"] -= 1
    check(saved["outcomes"] == metrics, "Terminal outcomes differ from independent classification")
    check(saved["exposure"] == {
        "assigned": sum(row["input"]["repeatAssigned"] for row in records),
        "assignedDispatched": counts["assigned_dispatched"], "eligible": counts["eligible"],
        "triggered": counts["triggered"], "repeated": counts["repeats"], "serverOverlap": counts["overlap"],
        "injectedTransportLosses": counts["injected"], "otherTransportFailures": counts["other"],
    }, "Exposure counts differ")
    check((saved["offered"], saved["dispatched"], saved["capacityMisses"], saved["windowMisses"], saved["httpAttempts"])
          == (len(records), len(dispatched), sum(x["miss"] == "capacity" for x in records),
              sum(x["miss"] == "window" for x in records), len(attempts)), "Offer/dispatch partition differs")
    expected_summary = {
        "clientLedgerMismatch": counts["mismatched_receipts"], "lateConfirmations": counts["inclusive_late"],
        "onTimeUniqueMatched": counts["matched_on_time"], "duplicateLogical": metrics["duplicate"],
        "zeroEffectLogical": counts["zero"], "oneEffectLogical": counts["one"],
        "committedEffects": sum(effects.values()), "extraFinancialEffects": counts["extra"], "hotDebits": counts["hot"],
    }
    check(all(saved[k] == val for k, val in expected_summary.items()), "Financial-effect or acknowledgement counts differ")
    expected_tps = len(latencies) / settings["measureSeconds"]
    check(math.isclose(saved["correctlyConfirmedTps"], expected_tps, abs_tol=1e-9), "Correct-confirmed TPS differs")
    for p, q in ((50, .50), (95, .95), (99, .99)):
        expected = percentile(latencies, q)
        actual = saved[f"p{p}Ms"]
        check((expected is None and actual is None) or (expected is not None and actual is not None and
              math.isclose(actual, expected, abs_tol=1e-7)), f"Logical P{p} differs")
    signature = hashlib.sha256(json.dumps(
        [(x["input"]["request"], x["input"]["repeatAttemptId"], x["input"]["repeatAssigned"])
         for x in records], default=str, sort_keys=True).encode()).hexdigest()
    return signature, raw["verifiedResetSha256"]


def verified_cells(root, phase):
    manifest_path = root / "study-checkpoint.json"
    checkpoint = json.loads(manifest_path.read_bytes())
    plan = checkpoint["binding"]["plan"]
    check(plan["phase"] == phase and checkpoint["complete"], "Expected a complete independently designated dataset")
    completed = [a for a in checkpoint["attempts"] if a["state"] == "complete"]
    check(len(completed) == plan["blocks"], "Incomplete block set")
    indexed = {}
    all_ids = set()
    for attempt in checkpoint["attempts"]:
        block = attempt["block"]
        scheduled = [c for c in plan["cells"] if c["block"] == block]
        signatures = {}
        resets = set()
        for item in attempt["failureEvidence"]:
            check(digest(relative_file(root, item["file"])) == item["sha256"], "Failure evidence hash differs")
        for slot, artifact in enumerate(attempt["runs"]):
            path = relative_file(root, artifact["file"])
            check(digest(path) == artifact["sha256"], f"Compressed raw hash differs for {path.name}")
            with gzip.open(path, "rt", encoding="utf-8") as src:
                raw = json.load(src, parse_float=Decimal)
            cell = scheduled[slot]
            check(raw["cell"] == cell and raw["runId"] not in all_ids, "Duplicate or non-frozen run cell/ID")
            all_ids.add(raw["runId"])
            summary = artifact["summary"]
            signature, reset = compare_summary(raw, summary)
            resets.add(reset)
            signatures.setdefault(cell["scenario"], set()).add(signature)
            if attempt["state"] == "complete":
                indexed[(block, cell["scenario"], cell["group"])] = artifact
        if attempt["state"] == "complete":
            check(all(len(signatures[s]) == 1 for s in SCENARIOS) and signatures["W2"] == signatures["W4"] and
                  len(resets) == 1, "The matched seed, offered requests or restored fixture differs")
    check(len(indexed) == plan["blocks"] * 16, "Not all preregistered cells were verified")
    return checkpoint, indexed, digest(manifest_path)


def factorial(points, metric):
    if any(x[metric] is None for x in points):
        return None
    a, b, c, d = (x[metric] for x in points)
    return {"occ": ((b - a) + (d - c)) / 2, "idempotency": ((c - a) + (d - b)) / 2,
            "interaction": d - c - b + a}


def analyse(root, phase, freeze_path):
    checkpoint, indexed, manifest_hash = verified_cells(root, phase)
    n = checkpoint["completedBlocks"]
    summaries = {key: item["summary"] for key, item in indexed.items()}
    treatment = {}
    contrasts = {}
    for scenario in SCENARIOS:
        treatment[scenario] = {}
        for group in GROUPS:
            runs = [summaries[b, scenario, group] for b in range(1, n + 1)]
            quantified = ("correctlyConfirmedTps", "p50Ms", "p95Ms", "p99Ms", "p95DispatchLagMs",
                          "httpAttemptTps", "peakLockWaiters", "peakDbSessions", "peakWorkingSetBytes",
                          "sampledProcessCpuSeconds")
            counts = ("offered", "dispatched", "capacityMisses", "windowMisses", "httpAttempts",
                      "duplicateLogical", "extraFinancialEffects", "zeroEffectLogical", "oneEffectLogical",
                      "committedEffects", "clientLedgerMismatch", "lateConfirmations", "versionConflicts",
                      "lockTimeouts", "lockWaiterSamples", "resourceSamples", "deadlocks", "internalRetries",
                      "keyBusy", "replays", "retryExhausted", "hotDebits", "clientActiveAtCutoff")
            outcome_counts = {k: sum(r["outcomes"][k] for r in runs) for k in runs[0]["outcomes"]}
            summed = {key: sum(r[key] for r in runs) for key in counts}
            dispatched = summed["dispatched"]
            treatment[scenario][group] = {
                "distributions": {key: interval([r[key] for r in runs if r[key] is not None]) for key in quantified},
                "counts": summed, "outcomes": outcome_counts,
                "failureRate": (outcome_counts["businessRejected"] + outcome_counts["operationalFailure"]) / dispatched if dispatched else None,
                "unknownRate": outcome_counts["unknown"] / dispatched if dispatched else None,
                "exposure": {k: sum(r["exposure"][k] for r in runs) for k in runs[0]["exposure"]},
                "auditViolationRuns": sum(bool(r["financialIssues"]) for r in runs),
                "keyContractViolationRuns": sum(r["persistentKeyViolation"] for r in runs),
                "points": {key: [r[key] for r in runs] for key in quantified},
            }
        contrasts[scenario] = {}
        for metric in ("correctlyConfirmedTps", "p95Ms"):
            estimates = [(b, factorial([summaries[b, scenario, g] for g in GROUPS], metric)) for b in range(1, n + 1)]
            contrasts[scenario][metric] = {
                effect: {"points": [{"block": b, "value": x[effect]} for b, x in estimates if x],
                         "interval": interval([x[effect] for _, x in estimates if x])}
                for effect in ("occ", "idempotency", "interaction")
            }

    w2 = contrasts["W2"]["correctlyConfirmedTps"]["interaction"]["points"]
    w4 = contrasts["W4"]["correctlyConfirmedTps"]["interaction"]["points"]
    check([p["block"] for p in w2] == [p["block"] for p in w4], "Interaction pairing mismatch")
    delta = [{"block": a["block"], "value": b["value"] - a["value"]} for a, b in zip(w2, w4)]
    rq3 = {"points": delta, "interval": interval([p["value"] for p in delta]), "classification": "pilot"}
    if phase == "formal":
        check(freeze_path and digest(freeze_path) == checkpoint["binding"]["freezeSha256"], "Formal freeze identity mismatch")
        freeze = json.loads(freeze_path.read_bytes())
        check(checkpoint["binding"]["plan"] == freeze["plan"], "Formal plan differs from freeze")
        precision = freeze["precision"]
        target = precision["targetHalfWidth"]
        rq3["targetHalfWidth"] = target
        half = rq3["interval"]["halfWidth"]
        rq3["classification"] = ("confirmatory" if precision["status"] == "confirmatory-candidate" and
                                 half is not None and half <= target else "exploratory")
    return {
        "phase": phase, "verifiedRuns": len(indexed), "completeBlocks": n, "manifestSha256": manifest_hash,
        "actualIndexedRuns": sum(len(attempt["runs"]) for attempt in checkpoint["attempts"]),
        "checkpointRecovery": checkpoint.get("recovery"),
        "invalidBlocks": checkpoint["invalidBlocks"], "reruns": checkpoint["reruns"],
        "measuredMachineSeconds": checkpoint["measuredMachineSeconds"], "budgetChargeSeconds": checkpoint["budgetChargeSeconds"],
        "archivalWriteSeconds": sum(x["rawWriteSeconds"] for x in indexed.values()),
        "localAnalysisSeconds": sum(x["analysisSeconds"] for x in indexed.values()),
        "timingSeconds": {k: sum(x["summary"]["timing"][k] for x in indexed.values()) for k in
                          ("setupSeconds", "resetSeconds", "warmupAndSettleSeconds",
                           "measurementAndDrainSeconds", "snapshotSeconds")},
        "scenario": treatment, "factorial": contrasts, "rq3": rq3,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("phase", choices=("engineering", "pilot", "formal"))
    parser.add_argument("dataset", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--freeze", type=Path)
    args = parser.parse_args()
    result = analyse(args.dataset.resolve(), args.phase, args.freeze)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, ensure_ascii=False, indent=2, allow_nan=False)
        stream.write("\n")
    print(f"Independent analysis: {result['verifiedRuns']} raw runs in {result['completeBlocks']} complete blocks")


if __name__ == "__main__":
    main()
