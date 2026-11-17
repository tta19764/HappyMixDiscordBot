"""Read-only Docker diagnostics. Never print container environment or runtime configuration."""
import json
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
        print(redact(json.dumps(summary), environment))
        logs = subprocess.run(["docker", "logs", "--tail", "200", container], text=True,
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT, check=False).stdout
        print(redact(logs, environment))


if __name__ == "__main__":
    main()
