# Official Releases (Single Source of Truth)

This directory is the **Single Source of Truth** for all built DASMO CYBER CAFE TOOLS installers.

## 📌 Standard Workflow:
1. Every successful build outputs its `.msi` here (e.g. `DASMO_CYBER_CAFE_TOOLS_Setup_v1.5.1.msi`).
2. **Install locally:** Double-click the `.msi` in this directory to install/test the app.
3. **Publish update:** Upload the `.msi` from this directory to the GitHub Release section.

## ⚠️ Rules:
- Never install from `src/.../bin/Release` or any intermediate folders.
- Always use the version-stamped installer here.
- Refer to [`../RELEASE.md`](../RELEASE.md) for full instructions.
