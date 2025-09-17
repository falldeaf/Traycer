import subprocess

# --- Config: emoji + repo path ---
repos = [
    ("🌱", r"C:\projects\SimX\case-files"),
    ("🧪", r"C:\projects\SimX\session-server"),
    ("🐍", r"C:\projects\SimX\unity-client")
]

# --- Script ---
output = []

for emoji, path in repos:
    try:
        branch = subprocess.check_output(
            ["git", "-C", path, "rev-parse", "--abbrev-ref", "HEAD"],
            stderr=subprocess.DEVNULL
        ).decode().strip()
        output.append(f"{emoji} {branch}")
    except subprocess.CalledProcessError:
        output.append(f"{emoji} ?")

print("  ".join(output))
