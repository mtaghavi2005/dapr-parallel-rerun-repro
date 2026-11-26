#!/usr/bin/env python3
"""Run the plain-Dapr reproduction and save shareable native-history evidence."""
import argparse
import json
import time
from datetime import datetime, timezone
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen

parser = argparse.ArgumentParser()
parser.add_argument("--url", default="http://localhost:5260")
parser.add_argument("--label", default="local")
args = parser.parse_args()


def request(path, body=None, method="GET"):
    data = None if body is None else json.dumps(body).encode()
    req = Request(args.url + path, data=data, method=method,
                  headers={"Content-Type": "application/json"})
    with urlopen(req, timeout=30) as response:
        return json.load(response)


def wait_for(instance_id, expected):
    deadline = time.monotonic() + 120
    while time.monotonic() < deadline:
        try:
            state = request("/api/runs/" + instance_id)
        except HTTPError as error:
            if error.code != 404:
                raise
            time.sleep(0.5)
            continue
        if state["status"] == expected:
            return state
        if state["status"] in ["Failed", "Completed", "Terminated"]:
            raise AssertionError(f"Expected {expected}, got {state['status']}")
        time.sleep(0.5)
    raise TimeoutError(f"Workflow did not reach {expected}")


source_id = request("/api/runs", {"delaySeconds": 3}, "POST")["sourceInstanceId"]
print("Source:", source_id, flush=True)
wait_for(source_id, "Failed")
accepted = request("/api/runs/" + source_id + "/rerun-inventory", method="POST")
rerun_id = accepted["rerunInstanceId"]
print("Rerun:", rerun_id, "Inventory schedule:", accepted["eventId"], flush=True)
wait_for(rerun_id, "Completed")
evidence = request(f"/api/compare?source={source_id}&rerun={rerun_id}")

history = evidence["sourceHistory"]
inventory_schedule = next(entry for entry in history if entry["eventType"] == "TaskScheduled"
                          and entry["activityName"] == "RerunInventoryActivity")
validate_schedule = next(entry for entry in history if entry["eventType"] == "TaskScheduled"
                         and entry["activityName"] == "RerunValidateActivity")
validate_completion = next(entry for entry in history if entry["eventType"] == "TaskCompleted"
                           and entry["activityName"] == "RerunValidateActivity")
assert validate_schedule["position"] < inventory_schedule["position"] < validate_completion["position"], \
    "Reproduction requires Validate scheduled before Inventory but completed after its schedule."

# Save the evidence even if this runtime behaves differently from the previously observed version.
stamp = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
directory = Path(__file__).resolve().parents[1] / "artifacts" / f"{args.label}-{stamp}"
directory.mkdir(parents=True, exist_ok=True)
(directory / "evidence.json").write_text(json.dumps({"accepted": accepted, **evidence}, indent=2) + "\n")
print("Evidence:", directory / "evidence.json", flush=True)

for row in evidence["rows"]:
    is_load = row["activityName"] == "RerunLoadRequestActivity"
    assert row["resultWasProducedInRerun"] is (not is_load), row
    if is_load:
        assert row["observedWorkerInvocationsInRerun"] == 0, row
    else:
        assert row["observedWorkerInvocationsInRerun"] >= 1, row
    if is_load:
        assert row["sameResultAsSource"] is True, row
    elif row["sourceInvocationId"] is not None:
        assert row["sameResultAsSource"] is False, row
    print(row["activityName"], "REUSED" if is_load else "EXECUTED AGAIN", flush=True)

print("PASS: Load reused; Validate, Inventory, Charge and Save executed in the rerun.")
print("Now compare Validate's actual execution with the dimmed node in Catalyst's graph.")
