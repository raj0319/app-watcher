"""Checks skills/*/SKILL.md against the Agent Skills specification's required fields.

Usage: python scripts/check_skills.py [skills-folder]
Exit code 0 when every skill passes.
Rules checked (from https://agentskills.io/specification):
  name: required, 1-64 chars, lowercase a-z 0-9 and hyphens, no leading/trailing/double hyphen, equals folder name
  description: required, 1-1024 chars
  compatibility: optional, 1-500 chars when present
  metadata: optional map of string -> string
"""
import os
import re
import sys

NAME_RE = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")


def parse_frontmatter(text):
    if not text.startswith("---"):
        return None, "no frontmatter"
    end = text.find("\n---", 3)
    if end < 0:
        return None, "frontmatter not closed"
    block = text[3:end].strip("\n")
    fields = {}
    current = None
    for line in block.splitlines():
        if not line.strip():
            continue
        if line.startswith(" ") or line.startswith("\t"):
            if current is None:
                return None, "indented line outside a map"
            k, _, v = line.strip().partition(":")
            fields[current][k.strip()] = v.strip().strip('"')
            continue
        key, _, value = line.partition(":")
        key = key.strip()
        value = value.strip()
        if value == "":
            fields[key] = {}
            current = key
        else:
            fields[key] = value.strip('"')
            current = None
    return fields, None


def check(skill_dir):
    problems = []
    path = os.path.join(skill_dir, "SKILL.md")
    if not os.path.isfile(path):
        return ["SKILL.md missing"]
    with open(path, encoding="utf-8") as fh:
        text = fh.read()
    fields, err = parse_frontmatter(text)
    if err:
        return [err]
    name = fields.get("name")
    if not isinstance(name, str) or not (1 <= len(name) <= 64) or not NAME_RE.match(name):
        problems.append("name missing or not 1-64 lowercase alphanumerics/hyphens")
    elif name != os.path.basename(os.path.normpath(skill_dir)):
        problems.append("name does not match the folder name")
    desc = fields.get("description")
    if not isinstance(desc, str) or not (1 <= len(desc) <= 1024):
        problems.append("description missing or not 1-1024 characters")
    comp = fields.get("compatibility")
    if comp is not None and not (isinstance(comp, str) and 1 <= len(comp) <= 500):
        problems.append("compatibility must be 1-500 characters when present")
    meta = fields.get("metadata")
    if meta is not None and (not isinstance(meta, dict) or not all(isinstance(v, str) for v in meta.values())):
        problems.append("metadata must be a map of string to string")
    body = text[text.find("\n---", 3) + 4:]
    if len(body.splitlines()) > 500:
        problems.append("body over 500 lines (specification recommends under 500)")
    return problems


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "skills")
    ok = True
    for entry in sorted(os.listdir(root)):
        d = os.path.join(root, entry)
        if not os.path.isdir(d):
            continue
        problems = check(d)
        if problems:
            ok = False
            print(f"FAIL {entry}: " + "; ".join(problems))
        else:
            print(f"OK   {entry}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
