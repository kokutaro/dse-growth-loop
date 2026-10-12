"""Disabled historical entry point. Frozen original bytes are data in provenance.json.
Use ../issue30_safety_supervisor.py only after its separate execution approval.
"""
import json
import sys


def main():
    print(json.dumps({"mode": "ARCHIVED; no Azure calls", "executionEnabled": False,
                      "stopConfirmed": False, "restorationConfirmed": False,
                      "liveAcceptance": "pending", "supportedEntryPoint": "../issue30_safety_supervisor.py"}))
    return 2 if len(sys.argv) > 1 else 0


if __name__ == "__main__":
    raise SystemExit(main())
