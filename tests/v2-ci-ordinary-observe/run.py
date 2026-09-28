#!/usr/bin/env python3
from __future__ import annotations

import base64
import hashlib
import importlib.util
import json
import pathlib
import unittest
from unittest.mock import patch


ROOT = pathlib.Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "v2_ci_ordinary_observe", ROOT / "tools/v2-ci-ordinary-observe.py"
)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


class AudioObservationSourceTests(unittest.TestCase):
    def test_tools_are_exact_committed_profile_bytes(self):
        expected = {
            "tools/v2-ci-ordinary-observe.py":
                "6e63f6f724fb267e77ef02f41b4c58a833e0dc5b08c003f07fd16c89e4f7fd55",
            "tools/v2-ci-ordinary-qualification.py":
                "304ce2894983dec1ed5f195bf58b0e2d01bceea68365d4e18296f00c068a3b2e",
        }
        for relative, digest in expected.items():
            self.assertEqual(digest, hashlib.sha256((ROOT / relative).read_bytes()).hexdigest())

    def test_audio_profile_is_fixed_and_cannot_activate_rehearsal(self):
        policy = json.loads((ROOT / "policy/v2-ci-ordinary-settlement.json").read_text())
        profile = MODULE.QUALIFICATION.source_profile(policy, "audio-v1")
        self.assertEqual("FS-GG/FS.GG.Audio", profile["repository"])
        self.assertEqual(1292226968, profile["repositoryId"])
        self.assertEqual(15368, profile["requiredCheckAppId"])
        self.assertEqual(5, len(profile["requiredChecks"]))
        self.assertEqual(4, len(profile["requiredGateChecks"]))
        with patch.object(MODULE.QUALIFICATION, "read_json", return_value=policy):
            with self.assertRaisesRegex(MODULE.QUALIFICATION.Refusal, "no rehearsal activation"):
                MODULE.observe({"FSGG_V2_SOURCE_PROFILE": "audio-v1"}, rehearsal=True)

    def test_audio_local_policy_is_admitted_before_runtime_event_fences(self):
        policy = json.loads((ROOT / "policy/v2-ci-ordinary-settlement.json").read_text())
        env = {
            "FSGG_V2_SOURCE_PROFILE": "audio-v1",
            "GITHUB_SHA": "a" * 40,
            "GITHUB_REPOSITORY": "FS-GG/FS.GG.Audio",
            "GITHUB_EVENT_NAME": "pull_request",
            "GITHUB_REF": "refs/heads/main",
        }
        repository = {
            "id": 1292226968,
            "full_name": "FS-GG/FS.GG.Audio",
            "default_branch": "main",
        }
        with patch.object(MODULE.QUALIFICATION, "read_json", return_value=policy), \
                patch.object(MODULE, "api", return_value=repository):
            with self.assertRaisesRegex(MODULE.QUALIFICATION.Refusal,
                                        "pinned protected-main event"):
                MODULE.observe(env)

    def test_current_authority_reads_audio_policy_and_workflow_from_one_main_revision(self):
        policy = json.loads((ROOT / "policy/v2-ci-ordinary-settlement.json").read_text())
        revision = "a" * 40

        def api(path):
            if path == "repos/FS-GG/FS.GG.Audio/git/ref/heads/main":
                return {"object": {"sha": revision}}
            prefix = "repos/FS-GG/FS.GG.Audio/contents/"
            if path.startswith(prefix) and path.endswith("?ref=" + revision):
                relative = path[len(prefix):].split("?ref=", 1)[0]
                return {
                    "encoding": "base64",
                    "content": base64.b64encode((ROOT / relative).read_bytes()).decode(),
                }
            raise AssertionError(path)

        with patch.object(MODULE, "api", side_effect=api):
            MODULE.current_authority("FS-GG/FS.GG.Audio", policy)

    def test_workflow_prepares_bounded_settlement_but_keeps_activation_receipt_guard(self):
        workflow = (ROOT / ".github/workflows/v2-ci-ordinary-settlement.yml").read_text()
        self.assertIn("  push:\n    branches: [main]", workflow)
        self.assertNotIn("    if: ${{ false }}", workflow)
        self.assertIn("if: needs.preflight.outputs.activation == 'true'", workflow)
        self.assertIn("environment: ordinary-v2", workflow)
        self.assertIn("FSGG_V2_SOURCE_PROFILE: audio-v1", workflow)
        self.assertIn("persist-credentials: false", workflow)
        self.assertIn("python3 tools/v2-ci-ordinary-observe.py produce", workflow)
        self.assertIn("python3 tools/v2-ci-ordinary-observe.py verify", workflow)
        self.assertIn("PACKAGE_VERSION: 0.1.3", workflow)
        self.assertIn(
            "PACKAGE_SHA256: c505159023f0740c885696ebe24f8e066e197d9cf09cc3e64050d4210bcd0cdb",
            workflow,
        )
        self.assertIn("ordinary-settlement execute", workflow)
        for name in (
            "V2_ORDINARY_APP_ID", "V2_ORDINARY_APP_PRIVATE_KEY",
            "V2_ORDINARY_AUTHORIZER_PRIVATE_KEY",
        ):
            self.assertIn("${{ secrets." + name + " }}", workflow)
        for forbidden in (
            "workflow_dispatch:",
            "repository_dispatch:",
            "pull_request:",
            "pull_request_target:",
            "V1_ADMISSION",
            "CALLABLE_ISOLATED_OPERATION",
        ):
            self.assertNotIn(forbidden, workflow)

    def test_policy_and_anchor_bind_source_and_shared_authority_scope(self):
        policy = json.loads((ROOT / "policy/v2-ci-ordinary-settlement.json").read_text())
        anchor = json.loads((ROOT / "policy/v2-ci-ordinary-settlement-anchor.json").read_text())
        self.assertEqual(
            {
                "profile": "audio-v1",
                "repository": "FS-GG/FS.GG.Audio",
                "repositoryId": 1292226968,
            },
            policy["selectedSource"],
        )
        self.assertEqual("7e1ac5f4689cbc8cd9b6db93b5ed7898c0c68319",
                         policy["sourceImplementation"]["commit"])
        self.assertEqual("audio-local-protected-policy-authority",
                         policy["sourceImplementation"]["adaptation"])
        self.assertFalse(policy["credentialJob"]["installed"])
        observation = policy["credentialJob"]["liveObservation"]
        self.assertTrue(observation["environmentPresent"])
        self.assertEqual(22908296207, observation["environmentId"])
        self.assertEqual("main", observation["customBranchPolicy"])
        self.assertEqual(1, observation["customBranchPolicyCount"])
        self.assertEqual(0, observation["secretCount"])
        self.assertEqual("published-served-verified", policy["packagePin"]["status"])
        self.assertEqual("0.1.3", policy["packagePin"]["version"])
        self.assertEqual(
            "c505159023f0740c885696ebe24f8e066e197d9cf09cc3e64050d4210bcd0cdb",
            policy["packagePin"]["sha256"],
        )
        self.assertEqual(36407941780, policy["packagePin"]["publishRunId"])
        self.assertEqual("FS.GG.Coordination.Cli.0.1.3.nupkg",
                         policy["packagePin"]["assetName"])
        self.assertTrue(policy["packagePin"]["servedPackageVerified"])
        self.assertTrue(all(item["provisioned"] is False
                            for item in policy["credentialInventory"]))
        self.assertEqual(5064713, anchor["writer"]["appId"])
        self.assertEqual(164553252, anchor["writer"]["installationId"])
        self.assertEqual("FS-GG/FS.GG.Coordination.Authority", anchor["writer"]["repository"])
        self.assertEqual(1351660651, anchor["writer"]["repositoryId"])
        self.assertEqual({"contents": "write", "metadata": "read"},
                         anchor["writer"]["permissions"])


if __name__ == "__main__":
    unittest.main()
