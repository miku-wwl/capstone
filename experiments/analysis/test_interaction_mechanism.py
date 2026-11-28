import unittest

from analyse_interaction_mechanism import confirmation_bridge, missing_duplicate_evidence, signed_confirmation
from audit_study_independent import classify, factorial, interval, nearest_rank


class MechanismTests(unittest.TestCase):
    def test_matched_duplicate_case_adds_negative_interaction_term(self):
        labels = {"A": "duplicate", "B": "correctlyConfirmed", "C": "correctlyConfirmed", "D": "correctlyConfirmed"}
        self.assertEqual(signed_confirmation(labels), -1)

    def test_two_sends_identify_the_noncommitting_attempt(self):
        failure = {"status": "retryExhausted", "lastFailure": "versionConflict"}
        success = {"status": "committed", "lastFailure": None}
        attempts = [{"attemptId": "first", "sendOrder": "first", "serverResult": failure},
                    {"attemptId": "second", "sendOrder": "repeat", "serverResult": success}]
        case = {"attempts": attempts, "ledgerEffects": [{"attemptId": "second"}]}
        self.assertEqual(missing_duplicate_evidence(case)["missingEffects"], [
            {"role": "first", "evidence": "retry budget exhausted with version conflict"}])
        attempts[0]["serverResult"] = {"status": "retryExhausted", "lastFailure": "lockTimeout"}
        self.assertEqual(missing_duplicate_evidence(case)["missingEffects"][0]["evidence"], "retryExhausted")

    def test_window_miss_and_server_trace_absence_are_distinct(self):
        self.assertEqual(missing_duplicate_evidence({"attempts": [], "dispatchMiss": "window"}),
                         {"observation": "first dispatch missed", "reason": "window"})
        case = {"attempts": [{"attemptId": "1", "sendOrder": "first", "serverResult": None},
                             {"attemptId": "2", "sendOrder": "repeat", "serverResult": None}],
                "ledgerEffects": [{"attemptId": "1"}]}
        self.assertEqual(missing_duplicate_evidence(case)["missingEffects"][0]["evidence"],
                         "no server admission retained")

    def test_count_bridge_preserves_signed_unconfirmed_terms(self):
        shared = dict(clientMismatch=0, unreconciled=0, late=0, unknown=0, businessRejected=0)
        a = dict(shared, dispatched=100, duplicate=20, operationalFailure=0, correctlyConfirmed=80)
        b = dict(shared, dispatched=101, duplicate=16, operationalFailure=2, correctlyConfirmed=83)
        self.assertEqual(confirmation_bridge(a, b), {
            "dispatchedDifference": 1, "effectOfDuplicateDifference": 4,
            "effectOfOperationalFailureDifference": -2, "effectOfOtherUnconfirmedDifference": 0,
            "correctlyConfirmedDifference": 3})

    def test_success_latency_excludes_balanced_duplicate_effects(self):
        row = {"input": {"request": {"logicalRequestId": "r"}},
               "attempts": [{"reply": {"status": "committed", "transferId": "t1"},
                             "httpStatus": 200, "sentMs": 2, "endedMs": 7}]}
        transfer = {"logicalRequestId": "r"}
        self.assertEqual(classify(row, [transfer], {"t1": transfer}, 8, True)[:2], ("correctlyConfirmed", 5))
        self.assertEqual(classify(row, [transfer, transfer], {"t1": transfer}, 8, True)[:2], ("duplicate", None))

    def test_factorial_and_student_t_estimators_on_small_vectors(self):
        self.assertEqual(factorial(1, 2, 3, 6), {"occ": 2, "idempotency": 3, "interaction": 2})
        stats = interval([1, 2, 3])
        self.assertAlmostEqual(stats["halfWidth"], 2.4841377117)
        self.assertEqual(stats["median"], 2)
        self.assertIsNone(interval([])["halfWidth"])
        self.assertIsNone(nearest_rank([], 95))
        self.assertEqual(nearest_rank([11, 2, 4], 95), 11)


if __name__ == "__main__":
    unittest.main()
