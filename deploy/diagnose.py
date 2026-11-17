"""Read-only Docker diagnostics. Never print container environment or runtime configuration."""
import json
import re
import subprocess
from urllib.parse import quote


def redact(text, environment):
    values = set()
    for item in environment:
        value = item.partition("=")[2]
        if len(value) >= 4:
            values.update((value, value.strip("\"'"), quote(value, safe="")))
    for value in sorted(values, key=len, reverse=True):
        if len(value) >= 4:
            text = text.replace(value, "[REDACTED]")
    return text


def select_logs(text):
    lines = text.splitlines()
    selected = set()
    for index, line in enumerate(lines):
        if re.search(r"warn:|fail:|exception|Invalid Form Body|BASE_TYPE_|Connected to Discord|Gateway:|Ready handler", line, re.IGNORECASE):
            selected.update(range(max(0, index - 1), min(len(lines), index + 6)))
    indices = sorted(selected)[:160]
    indices = sorted(set(indices).union(range(max(0, len(lines) - 40), len(lines))))
    return "\n".join(lines[index] for index in indices)


def main():
    ids = subprocess.check_output(["docker", "ps", "-aq", "--filter", "label=com.docker.compose.project=happymix",
                                   "--filter", "label=com.docker.compose.service=bot"], text=True).split()
    if not ids:
        print("No HappyMix container found.")
        return
    for container in ids:
        metadata = json.loads(subprocess.check_output(["docker", "inspect", container], text=True))[0]
        environment = metadata.get("Config", {}).get("Env", [])
        state = metadata.get("State", {})
        summary = {"container": container, "image": metadata.get("Config", {}).get("Image"),
                   "status": state.get("Status"), "exit_code": state.get("ExitCode"),
                   "oom_killed": state.get("OOMKilled"), "restart_count": metadata.get("RestartCount")}
        print(json.dumps(summary))
        logs = subprocess.run(["docker", "logs", "--tail", "5000", container], text=True,
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT, check=False).stdout
        print(redact(select_logs(logs), environment))


if __name__ == "__main__":
    main()
