# DASMO CYBER CAFE TOOLS

All-in-One Cyber Cafe Management, Document Processing & PDF Studio for Windows.

## 🚀 Overview

DASMO CYBER CAFE TOOLS is an enterprise-grade Windows desktop application built with .NET 8 WPF, designed specifically for Indian Cyber Cafe and CSC / Jan Seva Kendra operations:

- 📄 **PDF Editor Studio:** In-place text editing, whiteout/erasure, vector annotations, images, stamps, and signatures without loss of quality.
- 🔒 **Password-Protected PDF Support:** Automatic detection and unlocking for e-Aadhaar, e-Ration cards, bank statements, etc., with built-in Aadhaar password hint formula.
- 🪪 **Govt Card Automation:** Extraction and 1-click A4 multi-card sheet generation for Aadhaar, Voter ID, PAN, Ration, and Ayushman Bharat cards.
- 🗜️ **Smart Compression:** Advanced PDF, image, and document compression tailored for government portal upload limits (50KB, 100KB, 200KB, etc.).
- 🖼️ **Passport Photo Studio:** Instant passport photo generation with standard borders, 8/16/32-photo sheets, and background clean-up.
- 🔄 **Cloud License & In-App Auto-Update:** Instant cloud authorization with Firebase Firestore policy and GitHub Releases auto-updating.

---

## 📦 Releases & Installation

- Official installer is available in the [GitHub Releases](../../releases) section.
- For local builds, refer to [`RELEASE.md`](RELEASE.md).
- Single source of truth for release artifacts: `releases/`.

---

## 🛠️ Tech Stack

- **Framework:** .NET 8 WPF (Windows 10/11 x64)
- **PDF Engines:** PdfPig, PdfSharpCore, PDFium (Native)
- **Packaging:** WiX Toolset v5 (MSI Installer)
- **Backend / Auth:** Firebase Firestore REST API
- **Logging:** Serilog
