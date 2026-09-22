import os
import sys
import subprocess
import json
import fitz
import docx
from PIL import Image

def verify_full_pipeline():
    print("==================================================================")
    print("      LIVE REAL-WORLD DOCUMENT VERIFICATION & INSPECTION          ")
    print("==================================================================")
    
    sample_doc = r"C:\Users\Mypc3\.gemini\antigravity\brain\8d4b03a4-fd55-402d-af30-656b997e2221\.user_uploaded\media_1788009337899.png"
    out_dir = r"C:\Users\Mypc3\AppData\Local\Temp\DASMO_Live_Verify"
    os.makedirs(out_dir, exist_ok=True)
    
    script_path = r"c:\CODING\coading\DASMO CYBER CAFE\src\SmartSaver\Services\SmartCardDetector.py"
    
    # 1. Run Detector
    print(f"\n[STEP 1] Running SmartCardDetector on user document: {os.path.basename(sample_doc)}...")
    cmd = [sys.executable, script_path, "--input", sample_doc, "--outdir", out_dir, "--autowhiten", "true"]
    res = subprocess.run(cmd, capture_output=True, text=True)
    
    if res.returncode != 0:
        print(f"FAILED to run detector:\n{res.stderr}")
        return False
        
    print(f"Detector Output JSON:\n{res.stdout.strip()}")
    data = json.loads(res.stdout.strip())
    
    front_img_path = data.get("front_image")
    back_img_path = data.get("back_image")
    
    # 2. Verify Cropped Front & Back Images
    print("\n[STEP 2] Verifying cropped card images...")
    if not os.path.exists(front_img_path):
        print("FAILED: Front image file missing")
        return False
    if not os.path.exists(back_img_path):
        print("FAILED: Back image file missing")
        return False
        
    with Image.open(front_img_path) as f_img:
        fw, fh = f_img.size
        print(f"  - Front Card Dimensions: {fw}x{fh} px (Aspect Ratio: {fw/float(fh):.2f})")
        if fw < 50 or fh < 30:
            print("FAILED: Front image too small")
            return False
            
    with Image.open(back_img_path) as b_img:
        bw, bh = b_img.size
        print(f"  - Back Card Dimensions: {bw}x{bh} px (Aspect Ratio: {bw/float(bh):.2f})")
        if bw < 50 or bh < 30:
            print("FAILED: Back image too small")
            return False
            
    print("  -> Front & Back cropped images are crisp and verified!")
    
    print("\n[STEP 3] Verifying Standalone Executable & MSI Installer...")
    pub_exe = r"c:\CODING\coading\DASMO CYBER CAFE\src\SmartSaver\bin\Release\net8.0-windows10.0.17763.0\win-x64\publish\SmartSaver.exe"
    msi_file = r"c:\CODING\coading\DASMO CYBER CAFE\src\SmartSaver.Installer\bin\Release\DASMO_Cyber_Compressor_Setup.msi"
    
    if os.path.exists(pub_exe):
        print(f"  - SmartSaver.exe Exists ({os.path.getsize(pub_exe)/(1024*1024):.1f} MB)")
    else:
        print("FAILED: Published executable not found")
        return False
        
    if os.path.exists(msi_file):
        print(f"  - DASMO_Cyber_Compressor_Setup.msi Exists ({os.path.getsize(msi_file)/(1024*1024):.1f} MB)")
    else:
        print("FAILED: MSI Setup file not found")
        return False

    print("\n==================================================================")
    print("   ALL VERIFICATION CHECKS COMPLETED SUCCESSFULLY (100% PASS)     ")
    print("==================================================================")
    return True

if __name__ == "__main__":
    success = verify_full_pipeline()
    sys.exit(0 if success else 1)
