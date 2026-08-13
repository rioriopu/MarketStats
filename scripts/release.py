"""Market Stats のリリース用スクリプト。

ビルド成果物から配布 zip を作り、配布台帳（plugins.json）を成果物に合わせて更新する。

zip の中の manifest と配布台帳のバージョンがずれていると、
Dalamud 側でインストール・更新が失敗する。手作業だと順番を間違えやすいので、
「ビルド → zip → 台帳 → 検証」を一気にやる。

使い方:
    python scripts/release.py            # ビルドして zip と台帳を更新（転送はしない）
    python scripts/release.py --upload   # VPS への転送まで行う
"""

import argparse
import json
import os
import subprocess
import sys
import zipfile
from collections import OrderedDict

PROJECT_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BIN_DIR = os.path.join(PROJECT_DIR, "bin")
REPO_DIR = r"C:\Users\Administrator\TempRepos\PremiumDevReleaseRepo"
PLUGIN_NAME = "MarketStats"
FILES = [f"{PLUGIN_NAME}.dll", f"{PLUGIN_NAME}.json", f"{PLUGIN_NAME}.deps.json"]

# 外部ライブラリは同梱しない。
# 過去に SQLite ライブラリを同梱したところ、更新に失敗するようになったため
# （プラグイン本体以外の依存を配ると、読み込みの解決で問題が起きる）。
OPTIONAL_FILES: list[str] = []
NATIVE_FILES: list[tuple[str, str]] = []

SSH_KEY = "/c/Users/Administrator/.ssh/estelld_vps"
VPS = "root@133.167.127.79"
VPS_PROJECT = "/home/ubuntu/estelld-repo"


def build():
    print("== ビルド ==")
    result = subprocess.run(
        ["dotnet", "build", "-c", "Release", "-v", "m"],
        cwd=PROJECT_DIR, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if result.returncode != 0:
        print(result.stdout[-3000:])
        sys.exit("ビルドに失敗しました。")
    print("ビルド成功")


def pack():
    """ビルド成果物から zip を作る。成果物の manifest を正とする。"""
    manifest_path = os.path.join(BIN_DIR, f"{PLUGIN_NAME}.json")
    with open(manifest_path, encoding="utf-8-sig") as f:
        manifest = json.load(f)

    version = manifest["AssemblyVersion"]
    out_dir = os.path.join(REPO_DIR, "plugins", PLUGIN_NAME)
    os.makedirs(out_dir, exist_ok=True)
    zip_path = os.path.join(out_dir, "latest.zip")

    included = []

    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for name in FILES:
            path = os.path.join(BIN_DIR, name)
            if not os.path.exists(path):
                sys.exit(f"成果物が見つかりません: {path}")
            z.write(path, name)
            included.append(name)

        for name in OPTIONAL_FILES:
            path = os.path.join(BIN_DIR, name)
            if os.path.exists(path):
                z.write(path, name)
                included.append(name)

        for source, target in NATIVE_FILES:
            path = os.path.join(BIN_DIR, source)
            if os.path.exists(path):
                z.write(path, target)
                included.append(target)

    print(f"== zip 作成 == {zip_path} ({os.path.getsize(zip_path)} バイト / {version})")
    print(f"　収録: {len(included)} ファイル")
    return version, manifest, zip_path


def update_catalog(version, manifest):
    path = os.path.join(REPO_DIR, "plugins.json")
    with open(path, encoding="utf-8") as f:
        catalog = json.load(f, object_pairs_hook=OrderedDict)

    if PLUGIN_NAME not in catalog:
        sys.exit(f"台帳に {PLUGIN_NAME} のエントリがありません。")

    entry = catalog[PLUGIN_NAME]["manifest"]
    entry["AssemblyVersion"] = version
    entry["Description"] = manifest["Description"]
    entry["Punchline"] = manifest["Punchline"]

    with open(path, "w", encoding="utf-8") as f:
        f.write(json.dumps(catalog, ensure_ascii=False, indent=2) + "\n")

    print(f"== 台帳更新 == {version}")


def verify(zip_path):
    """出荷前チェック。zip の中身と台帳が食い違っていたら止める。"""
    with zipfile.ZipFile(zip_path) as z:
        broken = z.testzip()
        if broken:
            sys.exit(f"zip が壊れています: {broken}")
        inner = json.loads(z.read(f"{PLUGIN_NAME}.json").decode("utf-8-sig"))
        deps = json.loads(z.read(f"{PLUGIN_NAME}.deps.json").decode("utf-8"))

    with open(os.path.join(REPO_DIR, "plugins.json"), encoding="utf-8") as f:
        catalog_version = json.load(f)[PLUGIN_NAME]["manifest"]["AssemblyVersion"]

    deps_version = list(deps["libraries"].keys())[0].split("/")[1]

    print("== 検証 ==")
    print(f"  zip 内 manifest : {inner['AssemblyVersion']}")
    print(f"  deps            : {deps_version}")
    print(f"  配布台帳        : {catalog_version}")
    print(f"  API Level       : {inner['DalamudApiLevel']}")

    if not (inner["AssemblyVersion"] == catalog_version == deps_version):
        sys.exit("バージョンが一致していません。出荷を中止します。")

    print("  → すべて一致")


def upload():
    print("== 転送 ==")
    commands = [
        ["scp", "-i", SSH_KEY, "-o", "BatchMode=yes",
         os.path.join(REPO_DIR, "plugins.json").replace("\\", "/"),
         f"{VPS}:{VPS_PROJECT}/plugins.json"],
        ["scp", "-i", SSH_KEY, "-o", "BatchMode=yes",
         os.path.join(REPO_DIR, "plugins", PLUGIN_NAME, "latest.zip").replace("\\", "/"),
         f"{VPS}:{VPS_PROJECT}/plugins/{PLUGIN_NAME}/latest.zip"],
        ["ssh", "-i", SSH_KEY, "-o", "BatchMode=yes", VPS,
         f"chown -R ubuntu:ubuntu {VPS_PROJECT}/plugins {VPS_PROJECT}/plugins.json"],
    ]

    for command in commands:
        result = subprocess.run(command, capture_output=True, text=True)
        if result.returncode != 0:
            sys.exit(f"転送に失敗しました: {result.stderr}")

    print("転送完了")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--upload", action="store_true", help="VPS へ転送する")
    parser.add_argument("--skip-build", action="store_true", help="ビルドを省略する")
    args = parser.parse_args()

    if not args.skip_build:
        build()

    version, manifest, zip_path = pack()
    update_catalog(version, manifest)
    verify(zip_path)

    if args.upload:
        upload()
        print(f"\n{PLUGIN_NAME} {version} を公開しました。")
    else:
        print(f"\n{PLUGIN_NAME} {version} の準備ができました（--upload で転送）。")


if __name__ == "__main__":
    main()
