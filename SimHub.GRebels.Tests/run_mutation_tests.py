import subprocess
import sys
import re

MUTATIONS = [
    {
        "name": "Mutant 1: Ignore scale & bias in PacketParser",
        "file": "Protocol.cs",
        "find": "numbers[i] = raw * spec.Scale + spec.Bias;",
        "replace": "numbers[i] = raw;",
        "expected_failing_test": "PacketParser_ShouldDecodeAllNumericTypesCorrectly"
    },
    {
        "name": "Mutant 2: Invert surge collision front/rear direction check",
        "file": "TelemetryReceiver.cs",
        "find": "if (dvx < 0) { _lastCollisionDirection = \"Front\"; _lastCollisionDirectionCode = 1; }",
        "replace": "if (dvx > 0) { _lastCollisionDirection = \"Front\"; _lastCollisionDirectionCode = 1; }",
        "expected_failing_test": "Collision_ShouldDetectFrontImpact"
    },
    {
        "name": "Mutant 3: Invert heave bottom/top direction check",
        "file": "TelemetryReceiver.cs",
        "find": "if (dvz < 0) { _lastCollisionDirection = \"Bottom\"; _lastCollisionDirectionCode = 5; }",
        "replace": "if (dvz > 0) { _lastCollisionDirection = \"Bottom\"; _lastCollisionDirectionCode = 5; }",
        "expected_failing_test": "Collision_ShouldDetectTopAndBottomImpacts"
    },
    {
        "name": "Mutant 4: Bypass max weapon round delta ceiling (>10 rounds)",
        "file": "TelemetryReceiver.cs",
        "find": "if (delta >= 1.0 && delta <= 10.0)",
        "replace": "if (delta >= 1.0)",
        "expected_failing_test": "WeaponCount_AbnormalDelta_ShouldRebaseWithoutFiring"
    },
    {
        "name": "Mutant 5: Disable rising-edge check in edge detection",
        "file": "TelemetryReceiver.cs",
        "find": "bool rose = before < spec.Threshold && now >= spec.Threshold;",
        "replace": "bool rose = now >= spec.Threshold;",
        "expected_failing_test": "EdgeDetection_ShouldDebounceAndTriggerEdgeEvents"
    },
    {
        "name": "Mutant 6: Disable event queue capacity bounding clamp",
        "file": "TelemetryReceiver.cs",
        "find": "while (_pending.Count >= MaxPendingEvents)",
        "replace": "while (false)",
        "expected_failing_test": "DrainEvents_ShouldEnforceQueueMaxCapacity"
    },
    {
        "name": "Mutant 7: Disable port validation boundary check",
        "file": "Settings.cs",
        "find": "if (Port < 1 || Port > 65535)",
        "replace": "if (false)",
        "expected_failing_test": "Validate_InvalidPort_ShouldThrow"
    },
    {
        "name": "Mutant 8: Remove stale feed counter reset",
        "file": "TelemetryReceiver.cs",
        "find": "_prevPrimaryCount = null;",
        "replace": "// _prevPrimaryCount = null;",
        "expected_failing_test": "FeedStaleness_ResumedFeed_ShouldRebaselineWithoutFiringFalseEvents"
    }
]

def run_tests():
    cmd = ["dotnet", "test", "SimHub.GRebels.Tests/SimHub.GRebels.Tests.csproj", "-c", "Debug"]
    result = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    return result.returncode, result.stdout

def apply_mutation(file_path, find_str, replace_str):
    with open(file_path, "r") as f:
        content = f.read()
    if find_str not in content:
        raise ValueError(f"Could not find target string in {file_path}:\n{find_str}")
    new_content = content.replace(find_str, replace_str, 1)
    with open(file_path, "w") as f:
        f.write(new_content)

def revert_mutation(file_path, find_str, replace_str):
    with open(file_path, "r") as f:
        content = f.read()
    new_content = content.replace(replace_str, find_str, 1)
    with open(file_path, "w") as f:
        f.write(new_content)

def main():
    print("=" * 70)
    print("MUTATION & TEST EFFICACY VALIDATION RUNNER")
    print("=" * 70)

    # Verify baseline clean pass
    code, out = run_tests()
    if code != 0:
        print("ERROR: Baseline tests failed before running mutations!")
        sys.exit(1)
    print("Baseline test run PASSED (all tests passing before mutations).\n")

    total = len(MUTATIONS)
    killed = 0

    for idx, m in enumerate(MUTATIONS, 1):
        name = m["name"]
        file_path = m["file"]
        find_str = m["find"]
        replace_str = m["replace"]
        expected_test = m["expected_failing_test"]

        print(f"[{idx}/{total}] Testing {name} ...", end=" ", flush=True)

        try:
            apply_mutation(file_path, find_str, replace_str)
            test_code, test_output = run_tests()
        finally:
            subprocess.run(["git", "checkout", file_path], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

        if test_code != 0:
            if expected_test in test_output:
                print(f"KILLED (Expected failure '{expected_test}' caught the mutant)")
            else:
                print("KILLED (Test failed, but different test failed)")
            killed += 1
        else:
            print("SURVIVED! (WARNING: Test suite failed to catch mutant)")

    print("-" * 70)
    mutation_score = (killed / total) * 100.0
    print(f"Total Mutants Injected: {total}")
    print(f"Mutants Killed:        {killed}")
    print(f"Mutation Score:        {mutation_score:.1f}%")
    print("=" * 70)

    if killed == total:
        print("RESULT: 100% Mutation Kill Rate. Tests are highly efficacious and sensitive to real defects.")
        sys.exit(0)
    else:
        print("RESULT: Tests have efficacy gaps.")
        sys.exit(1)

if __name__ == "__main__":
    main()
