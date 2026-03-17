# SDLC Toolkit Setup

## Installation

Before using `sdlc-toolkit`, verify it is installed by running `sdlc-toolkit --version`.

### If `sdlc-toolkit` is not found

1. **Check for Python 3.10+**: Run `python --version`.
2. **Install the package** (requires user confirmation):
   ```bash
   pip install git+https://github.com/azure-core/sdlc-toolkit.git
   ```
   Do NOT install without confirmation.
3. **Verify**: Run `sdlc-toolkit --help` to confirm installation.

### If `sdlc-toolkit` is still not found after install

`pip` may have installed it to a directory not on your PATH.

```bash
python -m site --user-site
```

The `Scripts` folder is next to `site-packages`. Add it to your PATH:
- **Windows**: `C:\Users\<you>\AppData\Local\...\Python3xx\Scripts`
- **macOS/Linux**: `~/.local/bin`

## Optional: Azure OpenAI (for AI-powered analysis)

```bash
# For AI-powered design review and safety audits
export AZURE_OPENAI_ENDPOINT=https://your-resource.openai.azure.com/
export AZURE_OPENAI_API_KEY=your-key
export AZURE_OPENAI_DEPLOYMENT=gpt-4
```

## Optional: Azure DevOps Integration

```bash
# For ADO work item correlation
export ADO_ORG=your-org
export ADO_PROJECT=your-project
export ADO_PAT=your-personal-access-token
```

## Developer Setup (editable install)

If you need to modify the agents:

```bash
git clone https://github.com/azure-core/sdlc-toolkit.git
cd sdlc-toolkit
python -m venv .venv
.\.venv\Scripts\Activate.ps1   # Windows
# source .venv/bin/activate    # macOS/Linux
pip install -e ".[dev]"
```
