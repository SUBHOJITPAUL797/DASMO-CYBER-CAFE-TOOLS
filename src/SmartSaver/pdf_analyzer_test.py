import os
import sys
import json
import fitz  # PyMuPDF
import cv2
import numpy as np
from PIL import Image

def analyze_pdf_with_pymupdf(pdf_path, doc_type="auto"):
    """
    Intelligently detects government ID cards in PDF using text anchors, vector drawing paths, and bounding boxes.
    """
    doc = fitz.open(pdf_path)
    if len(doc) == 0:
        return None

    page = doc[0]
    page_rect = page.rect
    pw, ph = page_rect.width, page_rect.height

    # 1. Search for card keywords based on document type
    keywords_front = [
        "পশ্চিমবঙ্গ সরকার", "Digital Ration Card", "খাদ্য ও সরবরাহ দপ্তর", "Ration Card No", "NFSA",
        "Unique Identification Authority", "Mera Aadhaar", "GOVERNMENT OF INDIA", "Enrollment No",
        "INCOME TAX DEPARTMENT", "Permanent Account Number", "GOVT. OF INDIA",
        "ELECTION COMMISSION OF INDIA", "ELECTOR PHOTO IDENTITY CARD", "EPIC NO",
        "National Health Authority", "PM-JAY", "Ayushman Bharat"
    ]
    
    keywords_back = [
        "Help Desk", "Signature of Issuing Officer", "food.wb.gov.in", "Toll Free",
        "Address:", "Unique Identification Authority", "UIDAI", "1947",
        "यदि यह कार्ड मिलता है", "If this card is lost", "Download Date",
        "Electoral Registration Officer", "निर्वाचक रजिस्ट्रीकरण अधिकारी"
    ]

    text_instances = []
    for kw in keywords_front + keywords_back:
        rl = page.search_for(kw)
        if rl:
            for r in rl:
                text_instances.append((kw, r))

    print(f"Found {len(text_instances)} keyword text instances on PDF page.")

    # 2. Check for vector rectangles / drawing paths in bottom 60% of page
    drawings = page.get_drawings()
    card_rect_candidates = []

    for d in drawings:
        r = d["rect"]
        # Filter for rectangles in bottom half with reasonable card dimensions
        if r.y0 > 0.35 * ph and r.width > 0.25 * pw and r.height > 0.08 * ph:
            ar = r.width / r.height if r.height > 0 else 0
            # Double card AR ~ 2.5 - 3.5, Single card AR ~ 1.3 - 1.8
            if (1.2 <= ar <= 1.9) or (2.4 <= ar <= 3.8):
                card_rect_candidates.append(r)

    print(f"Found {len(card_rect_candidates)} vector drawing card candidates.")

    # 3. Determine bounding box for card
    detected_box = None

    if text_instances:
        # Filter text instances that are in the bottom region (cards are never in top header)
        bottom_texts = [r for kw, r in text_instances if r.y0 > 0.40 * ph]
        if bottom_texts:
            # Find the cluster of card text
            min_x = min(r.x0 for r in bottom_texts)
            max_x = max(r.x1 for r in bottom_texts)
            min_y = min(r.y0 for r in bottom_texts)
            max_y = max(r.y1 for r in bottom_texts)

            # Check if there is an enclosing vector drawing around this text
            enclosing = [cr for cr in card_rect_candidates if cr.x0 <= min_x + 15 and cr.x1 >= max_x - 15 and cr.y0 <= min_y + 15 and cr.y1 >= max_y - 15]
            if enclosing:
                # Pick the most tight enclosing vector rect
                detected_box = enclosing[0]
                print(f"Matched exact vector drawing rect: {detected_box}")
            else:
                # Expand slightly around text cluster to cover full card borders
                # Standard ID card height is ~0.35 to 0.45 of card width
                card_width = max_x - min_x
                # Add padding for header/footer margins
                pad_top = 18
                pad_bot = 18
                pad_side = 12
                detected_box = fitz.Rect(
                    max(0, min_x - pad_side),
                    max(0, min_y - pad_top),
                    min(pw, max_x + pad_side),
                    min(ph, max_y + pad_bot)
                )
                print(f"Computed text anchor cluster rect: {detected_box}")

    if detected_box is None and card_rect_candidates:
        # Pick the lowest card rectangle candidate
        card_rect_candidates.sort(key=lambda r: (r.y0, r.width * r.height), reverse=True)
        detected_box = card_rect_candidates[0]
        print(f"Selected vector candidate: {detected_box}")

    return detected_box

print("PDF Analyzer module ready.")
