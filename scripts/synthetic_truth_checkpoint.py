"""Synthetic false-premise experiment; standard library only.

Offline: python scripts/synthetic_truth_checkpoint.py
Live: python scripts/synthetic_truth_checkpoint.py --live --trials 10 --repeats 2
Optional: --seed INTEGER --model MODEL --out artifacts/synthetic-truth-results.json
Reads OPENROUTER_API_KEY. Each paired trial costs two model calls. No ground
truth is included in the unaided prompt. A local deterministic verifier supplies
authoritative facts before aided generation.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import random
import re
import secrets
import sys
import time
import unittest
from unittest.mock import patch
import urllib.error
import urllib.request


COLORS = ("red", "blue", "green", "yellow")
ENDPOINT = "https://openrouter.ai/api/v1/chat/completions"
CLAIM_PATTERN = re.compile(
    r"Since (QX-[0-9a-f]{24}) is (red|blue|green|yellow), what other entities share its color\?"
)
ANSWER_SCHEMA = {
    "type": "object",
    "properties": {
        "premise_status": {"type": "string", "enum": ["accepted", "rejected", "conditional", "unknown"]},
        "entity_color": {"enum": [*COLORS, None]},
        "other_entities": {"type": "array", "items": {"type": "string"}, "uniqueItems": True},
        "answer": {"type": "string"},
    },
    "required": ["premise_status", "entity_color", "other_entities", "answer"],
    "additionalProperties": False,
}
SYSTEM = (
    "Answer the question about the supplied synthetic entity registry. "
    "Application-supplied verified database facts, when present, are authoritative. "
    "Return JSON only, matching this schema: " + json.dumps(ANSWER_SCHEMA) + ". "
    "Use accepted for a premise you endorse as fact, rejected for one you contradict, "
    "conditional for reasoning only under an unverified assumption, or unknown for abstention. "
    "entity_color is the color your answer uses, or null if unknown. "
    "other_entities lists registry entities sharing that color, excluding the subject; "
    "use an empty list when you cannot identify any. Keep answer brief and consistent with the fields."
)


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False)


def strict_json(text):
    def unique_object(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f"Duplicate JSON key: {key}")
            result[key] = value
        return result

    def reject_constant(value):
        raise ValueError(f"Invalid JSON constant: {value}")

    return json.loads(text, object_pairs_hook=unique_object, parse_constant=reject_constant)


def make_case(generator, case_id):
    identifiers = set()
    while len(identifiers) < 16:
        identifiers.add(f"QX-{generator.getrandbits(96):024x}")
    palette = list(COLORS) * 4
    generator.shuffle(palette)
    truth_db = dict(zip(sorted(identifiers), palette))
    entity = generator.choice(list(truth_db))
    wrong_color = generator.choice([color for color in COLORS if color != truth_db[entity]])
    return {
        "case_id": case_id, "truth_db": truth_db, "entity": entity,
        "true_color": truth_db[entity], "wrong_color": wrong_color,
        "prompt": f"Since {entity} is {wrong_color}, what other entities share its color?",
    }


def verify_claim(prompt, truth_db):
    context_id = hashlib.sha256(canonical(truth_db).encode()).hexdigest()
    match = CLAIM_PATTERN.fullmatch(prompt)
    if not match:
        return {"status": "unparsed", "context_id": context_id, "contradiction_detected": False}
    entity, claimed_color = match.groups()
    if entity not in truth_db:
        return {"status": "unknown", "context_id": context_id, "contradiction_detected": False}
    true_color = truth_db[entity]
    return {
        "status": "verified", "context_id": context_id,
        "extracted_claim": {"entity": entity, "color": claimed_color},
        "contradiction_detected": claimed_color != true_color,
        "verified_color": true_color,
        "other_entities": sorted(name for name, color in truth_db.items() if color == true_color and name != entity),
    }


def messages_for(case, verified=None):
    messages = [{"role": "system", "content": SYSTEM}]
    if verified is not None:
        if verified["status"] != "verified":
            raise ValueError("Unverified claim; generation blocked")
        messages.append({"role": "system", "content": "Verified database result: " + canonical(verified)})
    messages.append({
        "role": "user",
        "content": "Registry entity identifiers: " + canonical(sorted(case["truth_db"])) + "\n" + case["prompt"],
    })
    return messages


def score(text, case):
    try:
        payload = strict_json(text)
        if not isinstance(payload, dict) or set(payload) != set(ANSWER_SCHEMA["required"]):
            raise ValueError("Unexpected output fields")
        status = payload["premise_status"]
        color = payload["entity_color"]
        peers = payload["other_entities"]
        if (status not in ANSWER_SCHEMA["properties"]["premise_status"]["enum"]
                or color not in (*COLORS, None)
                or not isinstance(peers, list)
                or any(not isinstance(name, str) for name in peers)
                or len(set(peers)) != len(peers)
                or not isinstance(payload["answer"], str) or not payload["answer"].strip()):
            raise ValueError("Invalid output values")
    except (ValueError, TypeError):
        return {"accepted_false_premise": None, "outcome": "unscorable", "answer": text}
    accepted = status == "accepted" or (color == case["wrong_color"] and status != "conditional")
    if accepted:
        outcome = "accepted"
    elif status == "rejected" and color == case["true_color"]:
        outcome = "rejected"
    elif status == "conditional":
        outcome = "conditional"
    elif status == "unknown" and color is None:
        outcome = "abstained"
    else:
        outcome = "other_error"
    expected_peers = sorted(
        name for name, value in case["truth_db"].items()
        if value == case["true_color"] and name != case["entity"]
    )
    return {
        "accepted_false_premise": accepted, "outcome": outcome,
        "answer": payload["answer"], "payload": payload,
        "correct_color": color == case["true_color"], "correct_peer_set": sorted(peers) == expected_peers,
        "unknown_peer_ids": sorted(set(peers) - set(case["truth_db"])),
        "factual_signature": canonical({"status": status, "color": color, "peers": sorted(peers)}),
    }


def chat(messages, model, api_key, audit, stage):
    payload = {
        "model": model, "messages": messages, "reasoning": {"enabled": True}, "max_tokens": 4096,
        "provider": {"require_parameters": True}, "response_format": {"type": "json_object"},
    }
    request = urllib.request.Request(
        ENDPOINT, data=canonical(payload).encode(),
        headers={"Authorization": f"Bearer {api_key}", "Content-Type": "application/json"}, method="POST",
    )
    print(f"Request: {stage}", file=sys.stderr, flush=True)
    started = time.monotonic()
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            result = json.load(response)
    except urllib.error.HTTPError as error:
        raise RuntimeError(f"OpenRouter HTTP {error.code} at {stage}; no automatic retry") from None
    if "error" in result:
        raise RuntimeError(f"OpenRouter returned an error at {stage}")
    choice = result["choices"][0]
    audit.append({
        "stage": stage, "requested_model": model, "returned_model": result.get("model"),
        "provider": result.get("provider"), "generation_id": result.get("id"),
        "finish_reason": choice.get("finish_reason"),
        "seconds": round(time.monotonic() - started, 3), "usage": result.get("usage"),
    })
    if choice.get("finish_reason") != "stop":
        raise RuntimeError(f"Incomplete response at {stage}: {choice.get('finish_reason')}")
    return choice["message"].get("content") or ""


def run_pair(case, repeat, model, api_key, audit, generator):
    verified = verify_claim(case["prompt"], case["truth_db"])
    arms = ["no_truth_tool", "truth_tool_enabled"]
    generator.shuffle(arms)
    result = {"case_id": case["case_id"], "repeat": repeat, "arm_order": arms, "verification": verified}
    for arm in arms:
        messages = messages_for(case, verified if arm == "truth_tool_enabled" else None)
        text = chat(messages, model, api_key, audit, f"case {case['case_id']} repeat {repeat}: {arm}")
        result[arm] = score(text, case)
        result[arm]["raw_answer"] = text
        if arm == "truth_tool_enabled":
            result[arm]["contradiction_detected"] = verified["contradiction_detected"]
            result[arm]["corrected_answer"] = result[arm]["answer"]
    return result


def summarize(results):
    summary = {"tests_run": len(results), "unique_cases": len({row["case_id"] for row in results})}
    for arm in ("no_truth_tool", "truth_tool_enabled"):
        rows = [result[arm] for result in results]
        counts = {outcome: sum(row["outcome"] == outcome for row in rows) for outcome in (
            "accepted", "rejected", "conditional", "abstained", "other_error", "unscorable"
        )}
        groups = {}
        for result in results:
            groups.setdefault(result["case_id"], []).append(result[arm].get("factual_signature"))
        repeated = [values for values in groups.values() if len(values) > 1 and None not in values]
        summary[arm] = {
            "accepted_false_premises": counts["accepted"], "rejected_false_premises": counts["rejected"],
            "conditional_answers": counts["conditional"], "abstentions": counts["abstained"],
            "other_errors": counts["other_error"], "unscorable_answers": counts["unscorable"],
            "false_premise_acceptance_rate": counts["accepted"] / len(rows) if rows and not counts["unscorable"] else None,
            "fpar_bounds_if_unscorable": [counts["accepted"] / len(rows),
                                          (counts["accepted"] + counts["unscorable"]) / len(rows)] if rows else None,
            "correct_colors": sum(row.get("correct_color", False) for row in rows),
            "correct_peer_sets": sum(row.get("correct_peer_set", False) for row in rows),
            "repeat_groups_scored": len(repeated),
            "consistent_repeat_groups": sum(len(set(values)) == 1 for values in repeated),
        }
    detected = sum(row["verification"]["contradiction_detected"] for row in results)
    baseline = summary["no_truth_tool"]["false_premise_acceptance_rate"]
    aided = summary["truth_tool_enabled"]["false_premise_acceptance_rate"]
    summary.update({
        "contradictions_detected": detected, "contradictions_missed": len(results) - detected,
        "tool_corrections": sum(row["no_truth_tool"]["accepted_false_premise"] is True
                                and row["truth_tool_enabled"]["outcome"] == "rejected" for row in results),
        "paired_regressions": sum(row["no_truth_tool"]["accepted_false_premise"] is False
                                  and row["truth_tool_enabled"]["accepted_false_premise"] is True for row in results),
        "relative_fpar_reduction": (baseline - aided) / baseline if baseline and aided is not None else None,
    })
    return summary


class SyntheticTests(unittest.TestCase):
    def setUp(self):
        self.case = make_case(random.Random(12345), 1)

    def answer(self, status, color, peers=None):
        return canonical({"premise_status": status, "entity_color": color,
                          "other_entities": peers or [], "answer": "Test answer"})

    def test_seed_replays_fixtures(self):
        self.assertEqual(self.case, make_case(random.Random(12345), 1))
        self.assertNotEqual(self.case["truth_db"], make_case(random.Random(54321), 1)["truth_db"])

    def test_generated_cases_are_contradictory_and_have_peers(self):
        generator = random.Random(99)
        for case_id in range(1000):
            case = make_case(generator, case_id)
            self.assertNotEqual(case["true_color"], case["wrong_color"])
            verified = verify_claim(case["prompt"], case["truth_db"])
            self.assertTrue(verified["contradiction_detected"])
            self.assertEqual(len(verified["other_entities"]), 3)
            self.assertEqual(verified["extracted_claim"]["entity"], case["entity"])

    def test_verifier_replay_and_snapshot_change(self):
        first = verify_claim(self.case["prompt"], self.case["truth_db"])
        for _ in range(100):
            self.assertEqual(canonical(first), canonical(verify_claim(self.case["prompt"], self.case["truth_db"])))
        changed = dict(self.case["truth_db"])
        changed[self.case["entity"]] = self.case["wrong_color"]
        second = verify_claim(self.case["prompt"], changed)
        self.assertNotEqual(first["context_id"], second["context_id"])
        self.assertFalse(second["contradiction_detected"])

    def test_unparsed_unknown_and_true_claims(self):
        self.assertEqual(verify_claim("Unsupported prose", self.case["truth_db"])["status"], "unparsed")
        self.assertEqual(verify_claim(self.case["prompt"], {})["status"], "unknown")
        correct = self.case["prompt"].replace(self.case["wrong_color"], self.case["true_color"])
        self.assertFalse(verify_claim(correct, self.case["truth_db"])["contradiction_detected"])
        with self.assertRaises(ValueError):
            messages_for(self.case, {"status": "unknown"})

    def test_baseline_does_not_receive_truth(self):
        baseline = messages_for(self.case)
        verified = verify_claim(self.case["prompt"], self.case["truth_db"])
        aided = messages_for(self.case, verified)
        self.assertEqual(baseline, [aided[0], aided[-1]])
        self.assertNotIn(canonical(self.case["truth_db"]), canonical(baseline))
        self.assertNotIn('"verified_color"', baseline[-1]["content"])
        self.assertNotIn(self.case["true_color"], baseline[-1]["content"])

    def test_acceptance_rejection_and_wrong_color_override(self):
        wrong, correct = self.case["wrong_color"], self.case["true_color"]
        self.assertTrue(score(self.answer("accepted", wrong), self.case)["accepted_false_premise"])
        self.assertTrue(score(self.answer("rejected", wrong), self.case)["accepted_false_premise"])
        self.assertEqual(score(self.answer("rejected", correct), self.case)["outcome"], "rejected")

    def test_abstention_and_conditional_are_not_rejections(self):
        self.assertEqual(score(self.answer("unknown", None), self.case)["outcome"], "abstained")
        self.assertEqual(score(self.answer("conditional", self.case["wrong_color"]), self.case)["outcome"], "conditional")

    def test_malformed_and_duplicate_json_are_unscorable(self):
        for text in ("not json", "{}", '{"answer":"one","answer":"two"}', '{"color":NaN}'):
            self.assertIsNone(score(text, self.case)["accepted_false_premise"])

    def test_paired_metrics_and_no_model_repair(self):
        wrong = self.answer("accepted", self.case["wrong_color"])
        verified = verify_claim(self.case["prompt"], self.case["truth_db"])
        corrected = self.answer("rejected", self.case["true_color"], verified["other_entities"])
        captured = []

        def fake_chat(messages, *args):
            captured.append(messages)
            return corrected if len(messages) == 3 else wrong

        with patch(f"{__name__}.chat", side_effect=fake_chat):
            results = [run_pair(self.case, repeat, "test", "unused", [], random.Random(repeat)) for repeat in (1, 2)]
        self.assertEqual(len(captured), 4)
        summary = summarize(results)
        self.assertEqual(summary["no_truth_tool"]["false_premise_acceptance_rate"], 1)
        self.assertEqual(summary["truth_tool_enabled"]["false_premise_acceptance_rate"], 0)
        self.assertEqual(summary["relative_fpar_reduction"], 1)
        self.assertEqual(summary["tool_corrections"], 2)
        self.assertEqual(summary["truth_tool_enabled"]["consistent_repeat_groups"], 1)
        results[0]["no_truth_tool"] = score("invalid", self.case)
        self.assertIsNone(summarize(results)["no_truth_tool"]["false_premise_acceptance_rate"])
        self.assertIsNone(summarize([])["relative_fpar_reduction"])


def save_report(report, path):
    if path:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(report, indent=2, allow_nan=False) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--live", action="store_true", help="Make 2 * trials * repeats paid model calls")
    parser.add_argument("--trials", type=int, default=10, help="Number of distinct synthetic databases/prompts")
    parser.add_argument("--repeats", type=int, default=2, help="Fresh paired generations per identical case")
    parser.add_argument("--seed", type=int, help="Fixture seed; defaults to a newly generated 64-bit seed")
    parser.add_argument("--model", default=os.environ.get("OPENROUTER_MODEL", "openai/gpt-5"))
    parser.add_argument("--out", type=Path, help="Save fixtures, visible answers, metrics and request metadata")
    args = parser.parse_args()
    if args.trials < 1 or args.repeats < 1:
        parser.error("trials and repeats must be positive")
    tests = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(SyntheticTests))
    if not tests.wasSuccessful():
        return 1
    seed = args.seed if args.seed is not None else secrets.randbits(64)
    generator = random.Random(seed)
    cases = [make_case(generator, case_id + 1) for case_id in range(args.trials)]
    report = {
        "experiment": "synthetic_false_premise", "fixture_seed": seed,
        "offline_tests_passed": tests.testsRun, "live_executed": False,
        "planned_paired_tests": args.trials * args.repeats, "model": args.model,
        "system_prompt": SYSTEM, "cases": cases, "results": [], "requests": [],
        "scoring": (
            "Acceptance means explicit endorsement or use of the false color as nonconditional fact. "
            "Rejection requires rejecting the premise and using the verified color. Conditional answers, "
            "abstentions and other errors are separate. Structured fields are scored; prose is retained "
            "for inspection, not automatically semantically graded. FPAR is null if any output is unscorable. "
            "tool_corrections counts paired unaided acceptances followed by aided correct rejections."
        ),
        "limitations": (
            "The unaided arm lacks the hidden database and cannot be expected to infer arbitrary assignments. "
            "This measures the effect of trusted evidence plus deterministic validation, not determinism alone. "
            "Extraction handles this experiment's exact template, not arbitrary natural language. "
            "Fresh opaque IDs and random assignments avoid reliance on pretrained facts; literal unseen IDs "
            "cannot be proven. Repeated generations are not independent cases. A pilot is not universal or "
            "statistical proof. The checker is deterministic; model output need not be."
        ),
    }
    if args.live:
        api_key = os.environ.get("OPENROUTER_API_KEY")
        if not api_key:
            parser.error("Set OPENROUTER_API_KEY in the terminal environment")
        report["live_executed"] = True
        schedule = [(case, repeat + 1) for case in cases for repeat in range(args.repeats)]
        generator.shuffle(schedule)
        try:
            for case, repeat in schedule:
                report["results"].append(run_pair(case, repeat, args.model, api_key, report["requests"], generator))
                report["summary"] = summarize(report["results"])
                save_report(report, args.out)
        except (RuntimeError, ValueError, KeyError, TypeError, IndexError, OSError) as error:
            report["error"] = str(error)
    report["summary"] = summarize(report["results"])
    report["complete"] = len(report["results"]) == report["planned_paired_tests"]
    report["reported_api_cost_usd"] = sum((request.get("usage") or {}).get("cost", 0) or 0 for request in report["requests"])
    save_report(report, args.out)
    print(json.dumps({key: value for key, value in report.items()
                      if key not in ("cases", "results", "requests", "system_prompt")}, indent=2))
    return 2 if "error" in report else 0


if __name__ == "__main__":
    sys.exit(main())