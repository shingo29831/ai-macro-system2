"""Module: @role: python -m ai_macro 実行時のパッケージメインエントリポイント"""

import sys
from ai_macro.cli import run_cli

if __name__ == "__main__":
    sys.exit(run_cli(sys.argv[1:]))
