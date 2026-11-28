"""Trace the post-hoc W2/W4 interaction decomposition to matched archived requests."""

import argparse
from collections import Counter, defaultdict
import json
from pathlib import Path

from audit_study_independent import GROUPS, Dataset, cell_evidence, digest, read_json, require, verify_cell


LABEL = "Post-hoc Exploratory Mechanism Analysis"


def indicator(label, outcome):
    return int(label == outcome)


def signed_confirmation(labels):
    a, b, c, d = (indicator(labels[g], "correctlyConfirmed") for g in GROUPS)
    return a - b - c + d


def request_trace(request_id, measurement):
    label, row, transactions = measurement["requests"][request_id]
    attempts = []
    for client in row["attempts"]:
        attempt_id = client["request"]["attemptId"]
        server = measurement["trace"].get(attempt_id)
        observed = server["attempt"] if server else None
        reply = client["reply"] or {}
        attempts.append({
            "attemptId": attempt_id, "sendOrder": "repeat" if client["repeat"] else "first",
            "sentMs": client["sentMs"], "endedMs": client["endedMs"], "httpStatus": client["httpStatus"],
            "responseStatus": reply.get("status"), "responseTransferId": reply.get("transferId"),
            "transportFailure": client["transportFailure"],
            "serverAdmittedMs": server["admittedMs"] if server else None,
            "serverObservedMs": server["observedMs"] if server else None,
            "serverResult": observed["result"] if observed else None,
            "injectedResponseLoss": observed["responseDropped"] if observed else None,
        })
    return {
        "outcome": label, "dispatchMiss": row["miss"], "attempts": attempts,
        "ledgerEffects": [{"attemptId": x["attemptId"], "transferId": x["transferId"]} for x in transactions],
    }


def missing_duplicate_evidence(observation):
    attempts = observation["attempts"]
    if not attempts:
        return {"observation": "first dispatch missed", "reason": observation["dispatchMiss"]}
    if len(attempts) == 1:
        attempt = attempts[0]
        return {"observation": "assigned repeat not sent", "firstOutcome": attempt["responseStatus"],
                "firstTransportFailure": attempt["transportFailure"]}
    committed = {row["attemptId"] for row in observation["ledgerEffects"]}
    absent = [a for a in attempts if a["attemptId"] not in committed]
    require(absent, "Duplicate comparison has no missing ledger effect")
    result = []
    for item in absent:
        server = item["serverResult"]
        if server is None:
            evidence = "no server admission retained"
        elif server["status"] == "retryExhausted" and server["lastFailure"] == "versionConflict":
            evidence = "retry budget exhausted with version conflict"
        else:
            evidence = server["status"]
        result.append({"role": item["sendOrder"], "evidence": evidence})
    return {"observation": "two client sends with absent financial effect", "missingEffects": result}


def categorize(description):
    result = description["observation"]
    if "reason" in description:
        result += f" ({description['reason']})"
    if "missingEffects" in description:
        result += " | " + ", ".join(x["role"] + ": " + x["evidence"] for x in description["missingEffects"])
    return result


def confirmation_bridge(reference, comparison):
    def difference(metric):
        return comparison[metric] - reference[metric]
    categories = ("clientMismatch", "unreconciled", "late", "unknown", "businessRejected")
    bridge = {
        "dispatchedDifference": difference("dispatched"),
        "effectOfDuplicateDifference": -difference("duplicate"),
        "effectOfOperationalFailureDifference": -difference("operationalFailure"),
        "effectOfOtherUnconfirmedDifference": -sum(difference(metric) for metric in categories),
    }
    require(sum(bridge.values()) == difference("correctlyConfirmed"), "The outcome partition does not reconcile")
    return {**bridge, "correctlyConfirmedDifference": difference("correctlyConfirmed")}


def compare_block(raw_by_group, block, seed, scenario):
    base = raw_by_group["A"]
    inputs = base["requests"]
    require(all(view["inputSignature"] == base["inputSignature"] and view["requests"].keys() == inputs.keys()
                for view in raw_by_group.values()), "Treatment cells have different offered identities")
    ab_discordance = Counter()
    cd_discordance = Counter()
    class_pairs = Counter()
    cases = []
    for uid, (_, row, _) in inputs.items():
        labels = {group: raw_by_group[group]["requests"][uid][0] for group in GROUPS}
        a_duplicate = labels["A"] == "duplicate"
        b_duplicate = labels["B"] == "duplicate"
        c_correct = labels["C"] == "correctlyConfirmed"
        d_correct = labels["D"] == "correctlyConfirmed"
        term = signed_confirmation(labels)
        if not term and a_duplicate == b_duplicate and c_correct == d_correct:
            continue
        observations = {g: request_trace(uid, raw_by_group[g]) for g in GROUPS}
        case = {"block": block, "scenario": scenario, "seed": seed, "logicalRequestId": uid,
                "offeredOrder": row["input"]["order"], "scheduledMs": row["scheduledMs"],
                "firstOfferedAttemptId": row["input"]["request"]["attemptId"],
                "repeatOfferedAttemptId": row["input"]["repeatAttemptId"], "repeatAssigned": row["input"]["repeatAssigned"],
                "confirmedInteractionCount": term, "observations": observations}
        if a_duplicate != b_duplicate:
            missing = "B" if a_duplicate else "A"
            description = missing_duplicate_evidence(observations[missing])
            ab_discordance[("A" if a_duplicate else "B") + " duplicate only"] += 1
            class_pairs[(missing, categorize(description))] += 1
            case["abDuplicateDifference"] = {"groupWithFewerEffects": missing, **description}
        if c_correct != d_correct:
            cd_discordance[f"C {labels['C']} / D {labels['D']}"] += 1
        cases.append(case)
    confirmed = {g: x["outcomes"]["correctlyConfirmed"] for g, x in raw_by_group.items()}
    duplicates = {g: x["outcomes"]["duplicate"] for g, x in raw_by_group.items()}
    total = confirmed["A"] - confirmed["B"] - confirmed["C"] + confirmed["D"]
    require(sum(row["confirmedInteractionCount"] for row in cases) == total,
            "Logical contributions fail to reconcile to the registered cell interaction")
    require(ab_discordance["A duplicate only"] - ab_discordance["B duplicate only"] == duplicates["A"] - duplicates["B"],
            "A/B discordances fail to reconcile to the difference")
    return {"confirmed": confirmed, "duplicates": duplicates, "ab": ab_discordance, "cd": cd_discordance,
            "evidence": class_pairs, "cases": cases, "interactionCount": total}


def scenario_summary(dataset, scenario):
    confirmed, duplicates = Counter(), Counter()
    cohorts = defaultdict(Counter)
    ab, cd, observations = Counter(), Counter(), Counter()
    blocks, cases = [], []
    signatures = {}
    for block in sorted(dataset.cells):
        cells = {c["group"]: c for c in dataset.cells[block] if c["scenario"] == scenario}
        groups = {}
        sources = {}
        for group in GROUPS:
            cell = cells[group]
            record, member = dataset.eligible[(block, scenario, group, cell["seed"])]
            raw = dataset.read_cell(cell, record, member)
            groups[group] = cell_evidence(raw, keep_requests=True)
            verify_cell(groups[group], record["summary"], member.name)
            sources[group] = {"runId": raw["runId"], "member": record["file"], "sha256": record["sha256"]}
            cohorts[group].update(groups[group]["counts"])
            cohorts[group].update(groups[group]["outcomes"])
            del raw
        seed = cells["A"]["seed"]
        signatures[block] = groups["A"]["inputSignature"]
        traced = compare_block(groups, block, seed, scenario)
        confirmed.update(traced["confirmed"])
        duplicates.update(traced["duplicates"])
        cases.extend(traced["cases"])
        ab.update(traced["ab"])
        cd.update(traced["cd"])
        observations.update(traced["evidence"])
        blocks.append({"block": block, "seed": seed, "offeredPerGroup": len(groups["A"]["requests"]),
                       "confirmed": traced["confirmed"], "duplicates": traced["duplicates"],
                       "interactionCount": traced["interactionCount"], "runs": sources})
    interaction = sum(row["interactionCount"] for row in blocks)
    return {
        "confirmedByGroup": dict(confirmed), "duplicatesByGroup": dict(duplicates), "cohortsByGroup": dict(cohorts),
        "aBMatchedDiscordance": {
            "aDuplicateOnly": ab["A duplicate only"], "bDuplicateOnly": ab["B duplicate only"],
            "observationsForGroupWithFewerEffects": [
                {"group": g, "evidence": label, "count": count} for (g, label), count in sorted(observations.items())]},
        "cDConfirmationDiscordance": dict(sorted(cd.items())),
        "confirmedDifferences": {"BminusA": confirmed["B"] - confirmed["A"], "DminusC": confirmed["D"] - confirmed["C"]},
        "confirmationBridges": {"BminusA": confirmation_bridge(cohorts["A"], cohorts["B"]),
                                "DminusC": confirmation_bridge(cohorts["C"], cohorts["D"])},
        "registeredInteractionCount": interaction, "blocks": blocks, "matchedCases": cases,
    }, signatures


def analyse(formal, freeze_path, published_path):
    frozen = Dataset(formal, "formal", freeze_path)
    published = read_json(published_path)
    require(frozen.sha256 == published["manifestSha256"], "The published contrast uses a different dataset")
    w2, sig_2 = scenario_summary(frozen, "W2")
    w4, sig_4 = scenario_summary(frozen, "W4")
    require(sig_2 == sig_4, "The two delivery scenarios use different original offered requests")
    measured_s = frozen.plan["settings"]["measureSeconds"]
    seconds = measured_s * len(frozen.cells)
    paired = []
    for left, right, expected in zip(w2["blocks"], w4["blocks"], published["rq3"]["points"], strict=True):
        require(left["seed"] == right["seed"] and left["block"] == right["block"] == expected["block"],
                "The scenarios do not have matched blocks")
        difference = right["interactionCount"] - left["interactionCount"]
        require(abs(difference / measured_s - expected["value"]) < 1e-9, "Block contrast differs from the published estimate")
        paired.append({"block": left["block"], "deltaCount": difference, "deltaTps": difference / measured_s})
    count = w4["registeredInteractionCount"] - w2["registeredInteractionCount"]
    require(sum(x["deltaCount"] for x in paired) == count and abs(count / seconds - published["rq3"]["interval"]["mean"]) < 1e-9,
            "Aggregate contrast differs from the published estimate")
    for scenario in (w2, w4):
        scenario["registeredInteractionTps"] = scenario["registeredInteractionCount"] / seconds
    return {
        "analysis": LABEL, "formalCheckpointSha256": frozen.sha256, "freezeSha256": digest(freeze_path),
        "codeCommit": frozen.freeze["codeCommit"],
        "matching": "Block, scenario, workload seed and logical request ID. Client and server attempts join by attempt ID.",
        "caseSelection": "All requests with nonzero confirmation-interaction contribution, A/B duplicate discordance, or C/D confirmation discordance.",
        "denominatorSecondsPerGroup": seconds, "scenarios": {"W2": w2, "W4": w4},
        "pairedBlocks": paired, "deltaCount": count, "deltaTps": count / seconds,
        "percentOfOfferedRate": 100 * count / seconds / frozen.plan["settings"]["offeredPerSecond"],
        "equivalentPerMeasurementWindow": count * measured_s / seconds,
        "limits": [
            "An absent ledger effect is traced to the observed attempt and terminal status.",
            "The archive contains retry counts and terminal reasons per HTTP attempt. It has no ordered trace of every SQL retry.",
            "Matching controls the offered workload. Realised schedules and neighbouring transactions can differ between cells.",
            "The traces do not identify which competing write caused each conflict or estimate a counterfactual mediation effect.",
        ],
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for argument in ("formal", "freeze", "published_analysis", "output"):
        parser.add_argument(argument, type=Path)
    args = parser.parse_args()
    result = analyse(args.formal, args.freeze, args.published_analysis)
    with args.output.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, ensure_ascii=False, indent=2, allow_nan=False)
        stream.write("\n")
    print(json.dumps({s: {k: d[k] for k in ("confirmedByGroup", "duplicatesByGroup", "aBMatchedDiscordance",
                                           "cDConfirmationDiscordance", "registeredInteractionCount")}
                      for s, d in result["scenarios"].items()}, ensure_ascii=False))


if __name__ == "__main__":
    main()
