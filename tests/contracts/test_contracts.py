"""Offline contract checks; these do not exercise application authorization or AI."""
import copy
import json
from pathlib import Path
import unittest

from jsonschema import Draft202012Validator, FormatChecker
from openapi_spec_validator import validate
from contract_rules import validate_invariants, normalize_term

ROOT = Path(__file__).resolve().parents[2]
SPEC = ROOT / "docs/specs"


def read(name):
    return json.loads((SPEC / name).read_text())


class ContractsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.schema = read("contracts.schema.json")
        cls.examples = read("examples.json")
        cls.taxonomy = read("taxonomy-v1.json")

    def validator(self, definition):
        return Draft202012Validator(
            {"$ref": f"#/$defs/{definition}", "$defs": self.schema["$defs"]},
            format_checker=FormatChecker(),
        )

    def test_generated_chat_assistance_contracts(self):
        path = ROOT / "tests/Dse.Tests/TestResults/chat-assistance-contract.json"
        self.assertTrue(path.exists(), "Run ChatAssistanceTests first")
        rows = json.loads(path.read_text())
        self.assertTrue(any(row["value"].get("messageKind") == "ai_case" for row in rows))
        for row in rows:
            self.validator(row["schema"]).validate(row["value"])

    def test_generated_profile_pipeline_contracts(self):
        path = ROOT / "tests/Dse.Tests/TestResults/profile-contract-fixture.json"
        if not path.exists():
            self.skipTest("Run ProfilePipelineTests first")
        for row in json.loads(path.read_text()):
            with self.subTest(schema=row["schema"], id=row["value"]["id"]):
                self.validator(row["schema"]).validate(row["value"])
                validate_invariants(row["schema"], row["value"], self.taxonomy)

    def test_knowledge_runtime_openapi(self):
        api = read("openapi-knowledge-v1.json")
        validate(api)
        self.assertEqual(10, len(api["paths"]))
        for path, methods in api["paths"].items():
            for method, operation in methods.items():
                self.assertTrue(operation["security"])
                if method == "post" and not path.endswith("knowledge-recovery"):
                    refs = [p.get("$ref") for p in operation["parameters"]]
                    self.assertIn("#/components/parameters/IdempotencyKey", refs)
                    self.assertIn("#/components/parameters/IfMatch", refs)

    def test_business_analysis_runtime_openapi(self):
        api = read("openapi-business-analysis-v1.json")
        validate(api)
        self.assertEqual(5, len(api["paths"]))
        for path, methods in api["paths"].items():
            for method, operation in methods.items():
                self.assertTrue(operation["security"])
                capability = path.endswith("capabilities")
                self.assertEqual([] if capability else ["member", "analyst"], operation["x-required-roles"])
                self.assertEqual(not capability, operation["x-requires-owner"])
                self.assertEqual(not capability, operation["x-revalidates-current-analysis-consent"])
                headers = operation["responses"]["200"]["headers"]
                self.assertEqual("no-store", headers["Cache-Control"]["schema"]["const"])
                self.assertEqual("ETag" in headers, not path.endswith(("{knowledgeId}", "capabilities")))
                if method in ("post", "patch"):
                    refs = [p.get("$ref") for p in operation["parameters"]]
                    self.assertIn("#/components/parameters/IdempotencyKey", refs)
                    self.assertEqual("#/components/parameters/IfMatch" in refs, method == "patch")
        start = api["paths"]["/api/v1/business-analysis"]["post"]
        self.assertIn("ANALYSIS_RECOVERY_REQUIRED", start["responses"]["409"]["description"])
        self.assertIn("ANALYSIS_LIVE_NOT_ENABLED", start["responses"]["501"]["description"])
        self.assertIn("AI_BUDGET_STOPPED", start["responses"]["409"]["description"])
        self.assertIn("ANALYSIS_LIVE_CONFIGURATION_INVALID", start["responses"]["422"]["description"])
        self.assertFalse(api["x-offline-recovery"]["httpEndpoint"])
        self.assertEqual(0, api["x-offline-recovery"]["refundJpy"])
        self.assertFalse(api["x-production-restrictions"]["automaticRetry"])
        schemas = api["components"]["schemas"]
        request = Draft202012Validator(schemas["StartRequest"], format_checker=FormatChecker())
        request.validate({"runId": "analysis-run-20261011", "cutoff": "2026-10-11T00:00:00Z", "configVersion": "business-analysis-demo-v1"})
        self.assertFalse(request.is_valid({"runId": "bad/id", "cutoff": "tomorrow", "configVersion": "live-v1"}))
        decision = Draft202012Validator(schemas["DecisionRequest"])
        decision.validate({"status": "investigating", "groupId": None})
        self.assertFalse(decision.is_valid({"status": "market_validated"}))
        capabilities = Draft202012Validator(schemas["Capabilities"])
        forbidden = {"allowed": False, "enabled": False, "configVersion": "business-analysis-live-v1", "mode": "live", "reason": "FORBIDDEN", "automaticRetry": False, "requestOwnerKey": None, "currentTime": None}
        capabilities.validate(forbidden)
        allowed = {**forbidden, "allowed": True, "enabled": True, "reason": None, "requestOwnerKey": "abr-" + "a" * 64, "currentTime": "2026-10-11T00:00:00Z"}
        capabilities.validate(allowed)
        self.assertFalse(capabilities.is_valid({**allowed, "requestOwnerKey": "principal-person-01"}))
        self.assertFalse(capabilities.is_valid({**forbidden, "requestOwnerKey": allowed["requestOwnerKey"]}))
        self.assertFalse(capabilities.is_valid({**forbidden, "enabled": True}))

    def test_workspace_contract_opaque_dtos_and_independent_name_consent(self):
        api = read("openapi-workspace-v1.json")
        validate(api)
        boundary = api["x-privacy-boundary"]
        self.assertTrue(boundary["structuredIdentifiersRemoved"])
        self.assertFalse(boundary["freeTextAnonymized"])
        self.assertTrue(boundary["casePublicationConsentIndependent"])
        self.assertTrue(boundary["identityDisclosureRequiresAllCurrentConsents"])
        for methods in api["paths"].values():
            for operation in methods.values():
                self.assertTrue(operation["security"])
                success = operation["responses"].get("200", operation["responses"].get("201"))
                self.assertEqual("no-store", success["headers"]["Cache-Control"]["schema"]["const"])
        components = api["components"]
        def check(name, value):
            return Draft202012Validator({"$ref": "#/components/schemas/" + name, "components": components}, format_checker=FormatChecker()).is_valid(value)
        room = "awr-" + "1" * 32
        alias = "awa-" + "2" * 32
        self.assertTrue(check("RoomId", room))
        self.assertFalse(check("RoomId", "demo-session-01"))
        participant = {"aliasId": alias, "aliasLabel": "協力者1", "sharedName": None}
        view = {"roomId": room, "reviewedVersion": "a" * 64, "fieldSet": "profile-display-name-v1", "etag": '"current"', "selfConsent": True, "state": "pending", "participants": [participant]}
        self.assertTrue(check("IdentityView", view))
        leaked = copy.deepcopy(view)
        leaked["participants"][0]["sharedName"] = "本人表示名"
        self.assertFalse(check("IdentityView", leaked), "Names cannot be disclosed in a pending consent view")
        for private_field in ("personId", "principalId", "ownerId", "tenantId", "sessionId", "sourceId"):
            self.assertFalse(check("IdentityView", {**view, private_field: "internal-id"}))
        consent = {"granted": True, "reviewedVersion": "a" * 64, "fieldSet": "profile-display-name-v1"}
        self.assertTrue(check("IdentityConsentRequest", consent))
        self.assertFalse(check("IdentityConsentRequest", {**consent, "personId": "someone-else"}))
        self.assertFalse(check("IdentityConsentRequest", {**consent, "publication": True}))
        candidate = {"token": "awt-" + "3" * 32, "aliasLabel": "協力候補1", "matchingRoutes": ["terms"]}
        self.assertTrue(check("Candidate", candidate))
        self.assertFalse(check("Candidate", {**candidate, "token": "person-01"}))
        self.assertFalse(check("Candidate", {**candidate, "personId": "person-01"}))
        metadata = {"departmentId": "awd-" + "4" * 32, "resolution": None, "reportedHours": None, "recurrences": None, "feedback": None}
        self.assertTrue(check("AnalysisMetadata", metadata))
        self.assertFalse(check("AnalysisMetadata", {**metadata, "departmentId": "internal-department"}))

    def test_operation_export_contract_keeps_partial_unknown_and_private_fields_out(self):
        api = read("openapi-workspace-v1.json")
        operation = api["paths"]["/api/v1/operation-measurements"]["get"]
        self.assertEqual(["operator"], operation["x-required-roles"])
        validator = Draft202012Validator({"$ref": "#/components/schemas/MeasurementExport", "components": api["components"]}, format_checker=FormatChecker())
        report = {"schemaVersion": "issue34-operation-export-v1", "runId": "a" * 32, "evidenceKind": "fixture", "state": "incomplete", "observedAt": "2026-10-11T00:00:00Z", "operations": [], "observedSdkRequestUnits": None, "completeRequestUnits": None, "completeMeasurement": False, "acceptanceGatePassed": False, "droppedOperations": 0, "missingExpectedKinds": [], "unknownComponents": ["invoice_reconciliation"], "limitations": ["Partial observations only"]}
        validator.validate(report)
        for patch in ({"completeMeasurement": True}, {"acceptanceGatePassed": True}, {"completeRequestUnits": 0}, {"unknownComponents": []}, {"tenantId": "internal"}, {"requestBody": "private"}):
            with self.subTest(patch=patch):
                self.assertFalse(validator.is_valid({**report, **patch}))

    def test_generated_knowledge_contracts(self):
        path = ROOT / "tests/Dse.Tests/TestResults/knowledge-contract-fixture.json"
        if not path.exists():
            self.skipTest("Run KnowledgeTests first")
        for row in json.loads(path.read_text()):
            with self.subTest(schema=row["schema"], id=row["value"]["id"]):
                self.validator(row["schema"]).validate(row["value"])
                validate_invariants(row["schema"], row["value"], self.taxonomy)

    def test_schema_and_every_named_example(self):
        Draft202012Validator.check_schema(self.schema)
        covered = set()
        for case in self.examples:
            with self.subTest(case=case["name"]):
                self.validator(case["schema"]).validate(case["value"])
                validate_invariants(case["schema"], case["value"], self.taxonomy)
                covered.add(case["schema"])
        required = set(self.schema["x-example-required"])
        self.assertFalse(required - covered, required - covered)

    def test_invalid_inputs_rejected_at_expected_field(self):
        cases = read("invalid-examples.json")
        self.assertGreaterEqual(len(cases), 20)
        for case in cases:
            with self.subTest(case=case["name"]):
                errors = list(self.validator(case["schema"]).iter_errors(case["value"]))
                self.assertTrue(errors, "invalid example was accepted")
                self.assertTrue(any(case["field"] in list(e.absolute_path) for e in errors)
                                or case["field"] == "$", errors)

    def test_openapi_and_local_references(self):
        api = read("openapi.json")
        validate(api)
        def walk(value):
            if isinstance(value, dict):
                if "$ref" in value:
                    self.assertTrue(value["$ref"].startswith("#/"))
                    target = api
                    for token in value["$ref"][2:].split("/"):
                        target = target[token.replace("~1", "/").replace("~0", "~")]
                for child in value.values():
                    walk(child)
            elif isinstance(value, list):
                for child in value:
                    walk(child)
        walk(api)
        for name, schema in self.schema["$defs"].items():
            expected = json.loads(json.dumps(schema).replace("#/$defs/", "#/components/schemas/"))
            self.assertEqual(api["components"]["schemas"][name], expected)

        for path, methods in api["paths"].items():
            for method, operation in methods.items():
                with self.subTest(path=path, method=method):
                    self.assertTrue(operation["security"])
                    for status in ("400", "401", "403", "404", "409", "412", "413", "429"):
                        self.assertIn(status, operation["responses"])
                    if method in ("post", "patch", "put"):
                        refs = [p.get("$ref") for p in operation["parameters"]]
                        self.assertIn("#/components/parameters/IdempotencyKey", refs)
                        if operation["x-requires-current-version"]:
                            self.assertIn("#/components/parameters/IfMatch", refs)

    def test_natural_search_v2_contract_and_generated_audit(self):
        api = read("openapi-search-v2.json")
        validate(api)
        path = ROOT / "tests/Dse.Tests/TestResults/natural-search-contract.json"
        if not path.exists():
            self.skipTest("Run NaturalSearchTests first")
        self.validator("RetrievalRun").validate(json.loads(path.read_text()))
        response = json.loads(path.with_name("natural-search-response.json").read_text())
        Draft202012Validator({"$ref": "#/components/schemas/Response", "components": api["components"]}).validate(response)

    def test_transition_contract_rejects_forbidden_actors_and_edges(self):
        transitions = read("transitions.json")
        for case in read("transition-examples.json"):
            with self.subTest(case=case):
                permitted = any(
                    row["from"] == case["from"] and row["to"] == case["to"]
                    and case["actor"] in row["actors"]
                    and set(row["guards"]).issubset(case["guards"])
                    for row in transitions[case["machine"]]
                )
                self.assertEqual(permitted, case["allowed"])

    def test_choice_keeps_complete_distribution_and_separate_confidence(self):
        categories = self.taxonomy["axes"][0]["categoryIds"]
        for case in self.examples:
            if case["schema"] != "ClassificationResult":
                continue
            value = case["value"]
            self.assertEqual(set(value["probabilities"]), set(categories))
            self.assertAlmostEqual(sum(value["probabilities"].values()), 1, places=6)
            self.assertIn(value["choice"], categories)
            self.assertIn("confidence", value)
        choice = next(c["value"] for c in self.examples if c["schema"] == "ClassificationResult")
        for category in categories:
            broken = copy.deepcopy(choice)
            del broken["probabilities"][category]
            self.assertFalse(self.validator("ClassificationResult").is_valid(broken))

    def test_actual_ai_run_trace_matches_contract(self):
        value = json.loads((ROOT / "tests/Dse.Tests/TestResults/ai-run.json").read_text())
        self.validator("AiRun").validate(value)
        validate_invariants("AiRun", value, self.taxonomy)
        self.assertEqual(value["trace"]["attemptCount"], 1)
        self.assertFalse(value["trace"]["usageEstimated"])

    def test_actual_disabled_accounting_trace_matches_contract_without_cost_claims(self):
        value = json.loads((ROOT / "tests/Dse.Tests/TestResults/ai-run-disabled.json").read_text())
        self.validator("AiRun").validate(value)
        validate_invariants("AiRun", value, self.taxonomy)
        self.assertEqual(value["trace"]["accountingMode"], "Disabled")
        self.assertIsNone(value["trace"]["estimatedCostJpy"])
        for mode, cost in [("Disabled", 0), ("Ledger", None), ("unknown", None)]:
            broken = copy.deepcopy(value)
            broken["trace"].update(accountingMode=mode, estimatedCostJpy=cost)
            self.assertFalse(self.validator("AiRun").is_valid(broken))

    def test_fixture_compatibility_uses_actual_domain_serialization(self):
        # Exported by FixtureContractTests; no second hand-copied fixture.
        fixture_path = ROOT / "tests/Dse.Tests/TestResults/contract-fixture.json"
        self.assertTrue(fixture_path.exists(), "Run dotnet test tests/Dse.Tests first")
        fixtures = json.loads(fixture_path.read_text())
        self.assertEqual(len(fixtures), 4)
        categories = set(self.taxonomy["axes"][0]["categoryIds"])
        for fixture in fixtures:
            self.validator("LegacySearchUnit").validate(fixture)
            self.assertEqual(fixture["taxonomyVersion"], self.taxonomy["version"])
            self.assertTrue({c["categoryId"] for c in fixture["classifications"]} <= categories)
            self.assertTrue({s["signalId"] for s in fixture["experienceSignals"]}
                            <= set(self.taxonomy["signalIds"]))

    def test_real_ingestion_records_match_contract(self):
        path = ROOT / 'tests/Dse.Tests/TestResults/ingestion-contract-fixture.json'
        self.assertTrue(path.exists(), 'Run dotnet test first')
        for row in json.loads(path.read_text()):
            with self.subTest(schema=row['schema']):
                self.validator(row['schema']).validate(row['value'])

    def test_real_conversation_records_match_contract(self):
        path = ROOT / 'tests/Dse.Tests/TestResults/conversation-contract-fixture.json'
        self.assertTrue(path.exists(), 'Run dotnet test first')
        for row in json.loads(path.read_text()):
            with self.subTest(schema=row['schema']):
                self.validator(row['schema']).validate(row['value'])

    def test_cross_field_invalid_examples(self):
        examples = {c["name"]: c for c in self.examples}
        mutations = [
            ("Document", {"activeVersion": 2}),
            ("Experience", {"period": {"start": "2026-09-23", "end": "2026-01-01", "precision": "day", "months": 8}}),
            ("Experience", {"period": {"start": None, "end": None, "precision": "unknown", "months": 0}}),
            ("ClassificationResult", {"probabilities": dict.fromkeys(self.taxonomy["axes"][0]["categoryIds"], 0.5)}),
            ("ClassificationResult", {"choice": "software"}),
            ("ClassificationResult", {"origin": "ai", "confidence": None}),
            ("ClassificationResult", {"aiRunId": "invented-run"}),
            ("Experience", {"correctedBy": None}),
            ("ExperienceSignal", {"evidenceRefs": []}),
            ("SearchUnit", {"classifications": [{"axis": "domain", "categoryId": "sales", "probability": 1}]}),
            ("SearchUnit", {"classifications": [{"axis": "domain", "categoryId": "sales", "probability": 0.7}, {"axis": "domain", "categoryId": "sales", "probability": 0.3}]}),
            ("AiRun", {"container": "conversations", "sessionId": None}),
            ("ai-stopped", {"errorCode": "AI_FAILED"}),
            ("ai-stopped", {"trace": {**examples["ai-stopped"]["value"]["trace"], "inputTokens": 10}}),
            ("Session", {"sessionId": "other-session"}),
            ("Message", {"authorType": "ai"}),
            ("Challenge", {"analysisConsent": "granted", "consentedVersion": None}),
            ("Challenge", {"analysisConsent": "granted", "consentedVersion": 2}),
            ("published-knowledge", {"approvedBy": None}),
            ("analysis-consented-knowledge", {"analysisConfirmations": []}),
            ("analysis-consented-knowledge", {"status": "withdrawn", "activeVersion": None}),
            ("published-knowledge", {"confirmations": []}),
            ("published-knowledge", {"confirmations": [{"personId": "demo-person-01", "version": 2, "scopeIds": ["demo"], "confirmedAt": "2026-09-23T00:00:00Z"}]}),
            ("withdrawn-knowledge", {"activeVersion": 1}),
            ("SearchResponse", {"code": "AI_FAILED"}),
            ("SearchResponse", {"candidates": [examples["SearchUnit"]["value"]]}),
        ]
        for name, patch in mutations:
            with self.subTest(name=name, patch=patch):
                case = examples[name]
                value = {**copy.deepcopy(case["value"]), **patch}
                self.validator(case["schema"]).validate(value)
                with self.assertRaises(ValueError):
                    validate_invariants(case["schema"], value, self.taxonomy)

    def test_normalization_dictionary_examples(self):
        dictionary = read("normalization-v1.json")
        for case in dictionary["examples"]:
            self.assertEqual(normalize_term(case["input"], dictionary), case["output"])

    def test_distinct_failure_and_unsaved_examples(self):
        examples = {c["name"]: c["value"] for c in self.examples}
        self.assertEqual(examples["correction-conflict"]["code"], "CORRECTION_CONFLICT")
        self.assertEqual(examples["withdrawn-knowledge"]["status"], "withdrawn")
        self.assertEqual(examples["notification-missed"]["notificationState"], "pending")
        self.assertEqual(examples["ai-stopped"]["status"], "budget_stopped")
        unsaved = examples["unsaved-message"]
        self.assertNotIn("id", unsaved)
        self.assertNotIn("sequence", unsaved)
        self.assertFalse(self.validator("Message").is_valid(unsaved))


if __name__ == "__main__":
    unittest.main()
