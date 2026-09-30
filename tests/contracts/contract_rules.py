"""Cross-field specification oracle. Not used by the running application."""
from datetime import date
import unicodedata


def validate_invariants(name, value, taxonomy):
    """JSON Schema validation must precede these additional contract constraints."""
    def require(condition, message):
        if not condition:
            raise ValueError(message)

    if "activeVersion" in value:
        require(value["activeVersion"] is None or value["activeVersion"] <= value["sourceVersion"],
                "activeVersion cannot exceed sourceVersion")
    if "period" in value:
        period = value["period"]
        if period["start"] and period["end"]:
            require(date.fromisoformat(period["start"]) <= date.fromisoformat(period["end"]),
                    "period is reversed")
        if period["precision"] == "unknown":
            require(all(period[key] is None for key in ("start", "end", "months")),
                    "unknown period cannot imply zero experience")
    if name == "ClassificationResult":
        require(abs(sum(value["probabilities"].values()) - 1) <= taxonomy["unknownPolicy"]["sumTolerance"],
                "Choice probabilities must sum to one")
        require(value["probabilities"][value["choice"]] == max(value["probabilities"].values()),
                "Choice must be one of the maximum probability categories")
        require(value["origin"] not in ("ai", "mock") or (value["confidence"] is not None and value["aiRunId"] is not None),
                "AI classification must retain Confidence and run provenance")
        require(value["origin"] != "fixture" or value["aiRunId"] is None,
                "legacy fixture must not invent an AI run")
    if name in ("Person", "Experience") and value["confirmationState"] != "ai_unconfirmed":
        require(value["correctedBy"] is not None and value["correctedBy"]["type"] == "principal",
                "self correction or confirmation must record its principal")
    if name == "ExperienceSignal" and value["signal"]["evidenceState"] == "supported":
        require(bool(value["evidenceRefs"]), "supported Noul requires evidence")
    if name == "SearchUnit":
        keys = [(c["axis"], c["categoryId"]) for c in value["classifications"]]
        require(len(keys) == len(set(keys)), "duplicate classification category")
        require(set(c["categoryId"] for c in value["classifications"]) ==
                set(taxonomy["axes"][0]["categoryIds"]), "projection must retain all categories")
    if name in ("Job", "AiRun"):
        require((value["container"] == "conversations") == (value["sessionId"] is not None),
                "container and session partition disagree")
    if name == "AiRun" and value["status"] == "budget_stopped":
        require(value["errorCode"] == "AI_BUDGET_STOPPED" and value["trace"]["modelId"] is None,
                "stopped-before-call run must not claim a model invocation")
        require(value["trace"]["inputTokens"] == value["trace"]["outputTokens"] == 0,
                "stopped-before-call run has no tokens")
    if name == "Session":
        require(value["id"] == value["sessionId"], "session id must equal partition key")
    if name == "Message" and value["authorType"] == "ai":
        require(bool(value["sourceSnapshots"]) and value["retrievalRunId"] is not None,
                "AI post requires sources and retrieval run")
    if name in ("Knowledge", "Challenge"):
        require((value["analysisConsent"] == "granted") == (value["consentedVersion"] is not None),
                "analysis consent must bind an explicit version")
        if value["analysisConsent"] == "granted":
            require(value["consentedVersion"] == value["sourceVersion"], "analysis consent is stale")
    if name == "Knowledge":
        if value["analysisConsent"] == "granted":
            approvals = value["analysisConfirmations"]
            require(value["status"] == "published" and len(approvals) == len(value["participantIds"]) and
                    {c["personId"] for c in approvals} == set(value["participantIds"]) and
                    all(c["version"] == value["sourceVersion"] and
                        set(c["scopeIds"]) == set(value["scopeIds"]) for c in approvals),
                    "analysis requires separate consent from all participants on the published version")
        if value["status"] == "published":
            require(value["approvedBy"] is not None and value["activeVersion"] == value["sourceVersion"],
                    "publication requires approver and active version")
            confirmations = value["confirmations"]
            require(len(confirmations) == len(value["participantIds"]) and
                    {c["personId"] for c in confirmations} == set(value["participantIds"]),
                    "all participants must confirm exactly once")
            require(all(c["version"] == value["publicationVersion"] and
                        set(c["scopeIds"]) == set(value["scopeIds"]) for c in confirmations),
                    "confirmation version or scope differs")
        else:
            require(value["activeVersion"] is None, "unpublished knowledge cannot be active")
    if name == "SearchResponse":
        require((value["status"] == "no_match") == (value["code"] == "NO_MATCH"),
                "no match must be distinguishable from failure")
        if value["status"] == "no_match":
            require(not value["candidates"], "no match cannot contain candidates")


def normalize_term(value, dictionary):
    normalized = unicodedata.normalize("NFKC", value).strip().lower()
    return dictionary["aliases"].get(normalized, normalized)
