#!/usr/bin/env python3
from __future__ import annotations

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
                "6081e1f499a618e3d019bd4508c630c6ee52d2bad018c8ecdda3e24c360e452f",
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

    def test_workflow_is_hard_disabled_and_has_no_credential_surface(self):
        workflow = (ROOT / ".github/workflows/v2-ci-ordinary-settlement.yml").read_text()
        self.assertIn("  push:\n    branches: [main]", workflow)
        self.assertIn("    if: ${{ false }}", workflow)
        self.assertIn("FSGG_V2_SOURCE_PROFILE: audio-v1", workflow)
        self.assertIn("persist-credentials: false", workflow)
        self.assertIn("python3 tools/v2-ci-ordinary-observe.py produce", workflow)
        for forbidden in (
            "secrets.",
            "environment:",
            "ordinary-settlement execute",
            "PACKAGE_VERSION",
            "PACKAGE_SHA256",
            "workflow_dispatch:",
            "repository_dispatch:",
            "pull_request:",
            "pull_request_target:",
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
        self.assertFalse(policy["credentialJob"]["installed"])
        self.assertFalse(policy["credentialJob"]["liveObservation"]["environmentPresent"])
        self.assertEqual(0, policy["credentialJob"]["liveObservation"]["repositorySecretCount"])
        self.assertEqual(5064713, anchor["writer"]["appId"])
        self.assertEqual(164553252, anchor["writer"]["installationId"])
        self.assertEqual("FS-GG/FS.GG.Coordination.Authority", anchor["writer"]["repository"])
        self.assertEqual(1351660651, anchor["writer"]["repositoryId"])
        self.assertEqual({"contents": "write", "metadata": "read"},
                         anchor["writer"]["permissions"])


if __name__ == "__main__":
    unittest.main()
