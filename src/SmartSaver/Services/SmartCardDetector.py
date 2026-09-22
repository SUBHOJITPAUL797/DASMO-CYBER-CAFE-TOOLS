#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
DASMO CYBER CAFE TOOLS - Industrial Precision Smart Card Vision Extractor
Powered by OpenCV & PyMuPDF (100% On-Device Computer Vision)
Extracts pixel-perfect, edge-to-edge Front and Back cards from official Indian Government documents:
- West Bengal / NFSA Digital e-Ration Card (Both Blue Background & White Background variants)
- UIDAI e-Aadhaar PDF
- Election Commission e-EPIC Voter Card
- Income Tax Department e-PAN Card
- PM-JAY Ayushman Bharat / ABHA Health Card
- Scanned single/double images and multi-page family batches (1 to 100+ cards)
"""

import sys
import os
import json
import argparse
import traceback
import random

try:
    import fitz  # PyMuPDF
except ImportError:
    fitz = None

try:
    import cv2
    import numpy as np
except ImportError:
    cv2 = None
    np = None


def auto_whiten_image(np_bgr):
    """Whitens grayish/yellowish paper background while preserving text, photos, and seals."""
    if cv2 is None or np_bgr is None or np_bgr.size == 0:
        return np_bgr

    lab = cv2.cvtColor(np_bgr, cv2.COLOR_BGR2LAB)
    l, a, b = cv2.split(lab)
    mask = l > 215
    l[mask] = np.clip(l[mask].astype(np.float32) * 1.15, 0, 255).astype(np.uint8)
    enhanced_lab = cv2.merge([l, a, b])
    return cv2.cvtColor(enhanced_lab, cv2.COLOR_LAB2BGR)


def add_subtle_card_border(card_bgr, border_color=(212, 212, 212), min_whiteness=230):
    """
    If the perimeter of the card is white / near-white, draws a subtle 1px crisp border
    so that when printed on physical white A4 paper, the card edges are cleanly visible
    and easy to cut with scissors.
    """
    if cv2 is None or card_bgr is None or card_bgr.size == 0:
        return card_bgr
    h, w = card_bgr.shape[:2]
    if h < 20 or w < 20:
        return card_bgr

    gray = cv2.cvtColor(card_bgr, cv2.COLOR_BGR2GRAY)
    top = gray[:2, :]
    bot = gray[-2:, :]
    left = gray[:, :2]
    right = gray[:, -2:]
    perimeter_mean = (np.mean(top) + np.mean(bot) + np.mean(left) + np.mean(right)) / 4.0

    if perimeter_mean >= min_whiteness:
        out = card_bgr.copy()
        cv2.rectangle(out, (0, 0), (w - 1, h - 1), border_color, 1)
        return out
    return card_bgr


def trim_and_clean_card_edges(card_bgr):
    """
    Precision Edge Cleaner:
    1. Scans the perimeter (top, bottom, left, right) for leftover dashed cutting guide lines or blade marks.
    2. Immediately stops if the edge is already clean whitespace, preventing false-positive cropping into text.
    3. Preserves internal card content, margins, and headers without over-trimming.
    """
    if cv2 is None or card_bgr is None or card_bgr.size == 0:
        return card_bgr

    h, w = card_bgr.shape[:2]
    if h < 30 or w < 30:
        return card_bgr

    gray = cv2.cvtColor(card_bgr, cv2.COLOR_BGR2GRAY)
    max_scan_y = min(int(h * 0.035), 18)
    max_scan_x = min(int(w * 0.035), 18)

    top_crop = 0
    for y in range(max_scan_y):
        row = gray[y, :]
        transitions = np.sum(np.abs(np.diff((row < 180).astype(int))))
        dark_count = np.sum(row < 160)
        # If the edge row is clean whitespace, card margin is intact -> stop immediately
        if transitions < 6 and dark_count < int(w * 0.03):
            break
        # Only trim if it's an edge artifact (dashed line or black border)
        if transitions > 25 or dark_count > int(w * 0.45):
            top_crop = y + 1

    bot_crop = h
    for y in range(h - 1, max(h - 1 - max_scan_y, 0), -1):
        row = gray[y, :]
        transitions = np.sum(np.abs(np.diff((row < 180).astype(int))))
        dark_count = np.sum(row < 160)
        if transitions < 6 and dark_count < int(w * 0.03):
            break
        if transitions > 25 or dark_count > int(w * 0.45):
            bot_crop = y

    left_crop = 0
    for x in range(max_scan_x):
        col = gray[:, x]
        transitions = np.sum(np.abs(np.diff((col < 180).astype(int))))
        dark_count = np.sum(col < 160)
        if transitions < 6 and dark_count < int(h * 0.03):
            break
        if transitions > 25 or dark_count > int(h * 0.45):
            left_crop = x + 1

    right_crop = w
    for x in range(w - 1, max(w - 1 - max_scan_x, 0), -1):
        col = gray[:, x]
        transitions = np.sum(np.abs(np.diff((col < 180).astype(int))))
        dark_count = np.sum(col < 160)
        if transitions < 6 and dark_count < int(h * 0.03):
            break
        if transitions > 25 or dark_count > int(h * 0.45):
            right_crop = x

    if (bot_crop - top_crop > 40) and (right_crop - left_crop > 40):
        card_bgr = card_bgr[top_crop:bot_crop, left_crop:right_crop]

    return card_bgr


def finalize_card(card_bgr, auto_whiten=True, trim_edges=True):
    """Whitens, trims dashed scissors marks, and preserves crisp clean card boundaries."""
    if card_bgr is None or card_bgr.size == 0:
        return card_bgr
    if auto_whiten:
        card_bgr = auto_whiten_image(card_bgr)
    if trim_edges:
        card_bgr = trim_and_clean_card_edges(card_bgr)
    return card_bgr


def detect_cards_universal(bgr, full_text="", doc_type="auto"):
    """
    Universal Computer Vision Engine for Indian Government Cards:
    - Scans entire document without hardcoded bottom-only assumptions.
    - Bridges dashed cutting lines with adaptive directional morphological kernels.
    - Detects Twin Cards (Voter ID e-EPIC), Single Cards (PAN, ABHA, DL),
      Double-card blocks (Aadhaar, joined Ration cards), and pre-cropped cards.
    """
    if cv2 is None or bgr is None or bgr.size == 0:
        return None

    h, w = bgr.shape[:2]
    ar_page = w / float(h) if h > 0 else 1.0
    doc_type_upper = doc_type.upper()
    text_lower = full_text.lower()

    # 1. Pre-cropped ID card check (Already an isolated card)
    if (h < 650 or w < 1150) and (1.25 <= ar_page <= 1.95):
        return {
            "method": "pre_cropped_card",
            "front": bgr,
            "back": None
        }

    # 2. Document Type Clues
    is_ration = "RATION" in doc_type_upper or "food.wb.gov.in" in text_lower or "digital ration" in text_lower or "khadya" in text_lower
    is_aadhaar = "AADHAAR" in doc_type_upper or "unique identification" in text_lower or "mera aadhaar" in text_lower or "uidai" in text_lower
    is_voter = "VOTER" in doc_type_upper or "election commission" in text_lower or "elector photo" in text_lower or "epic" in text_lower
    is_pan = "PAN" in doc_type_upper or "income tax department" in text_lower or "permanent account number" in text_lower
    is_ayushman = "AYUSHMAN" in doc_type_upper or "ABHA" in doc_type_upper or "PMJAY" in doc_type_upper or "health authority" in text_lower or "national health" in text_lower or "abha" in text_lower

    # -------------------------------------------------------------------------
    # STRATEGY 1: WEST BENGAL E-RATION CARD (Precision Dashed Cutting Frame Engine)
    # -------------------------------------------------------------------------
    if is_ration or ("wb" in text_lower and any(k in text_lower for k in ["খাদ্য", "sphh", "rksy", "phh", "aay", "ration"])):
        # West Bengal Digital e-Ration cards place front & back cards side-by-side in the lower page region.
        # Official dashed cutting guide lines enclose the cards:
        # Top cut line: ~0.778 * h
        # Bottom cut line: ~0.971 * h
        # Left cut line: ~0.069 * w
        # Center divider: ~0.502 * w
        # Right cut line: ~0.931 * w
        gray = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)

        # 1. Detect top horizontal dashed line (scanning 0.760*h to 0.795*h)
        top_dash_y = None
        for y in range(int(0.760 * h), int(0.795 * h)):
            row = gray[y, :] < 180
            l_trans = np.sum(np.abs(np.diff(row[:w // 2].astype(int))))
            r_trans = np.sum(np.abs(np.diff(row[w // 2:].astype(int))))
            if l_trans >= 45 and r_trans >= 45 and (l_trans + r_trans) >= 130:
                if np.mean(gray[max(0, y - 5):max(0, y - 1), :]) > 210:
                    top_dash_y = y
                    break
        if top_dash_y is None:
            top_dash_y = int(0.778 * h)

        # 2. Detect bottom horizontal dashed line (scanning 0.955*h to 0.985*h)
        bot_dash_y = None
        for y in range(int(0.955 * h), int(0.985 * h)):
            row = gray[y, :] < 180
            l_trans = np.sum(np.abs(np.diff(row[:w // 2].astype(int))))
            r_trans = np.sum(np.abs(np.diff(row[w // 2:].astype(int))))
            if l_trans >= 45 and r_trans >= 45 and (l_trans + r_trans) >= 130:
                if np.mean(gray[min(h - 1, y + 1):min(h - 1, y + 5), :]) > 210:
                    bot_dash_y = y
        if bot_dash_y is None:
            bot_dash_y = int(0.971 * h)

        # 3. Detect vertical cutting guide lines in the card band
        card_band = gray[top_dash_y:bot_dash_y, :]
        left_x = None
        for x in range(int(0.05 * w), int(0.09 * w)):
            col = card_band[:, x] < 180
            if np.sum(np.abs(np.diff(col.astype(int)))) > 70:
                left_x = x
                break
        if left_x is None: left_x = int(0.069 * w)

        center_x = None
        for x in range(int(0.48 * w), int(0.53 * w)):
            col = card_band[:, x] < 180
            if np.sum(np.abs(np.diff(col.astype(int)))) > 70:
                center_x = x
                break
        if center_x is None: center_x = int(0.502 * w)

        right_x = None
        for x in range(int(0.91 * w), int(0.95 * w)):
            col = card_band[:, x] < 180
            if np.sum(np.abs(np.diff(col.astype(int)))) > 70:
                right_x = x
                break
        if right_x is None: right_x = int(0.931 * w)

        # Inward crop strictly 4px inside the dashed cutting lines:
        # Removes 100% of the dashed scissor guide marks while preserving generous whitespace margins for header and footer!
        front_raw = bgr[top_dash_y + 4 : bot_dash_y - 4, left_x + 4 : center_x - 4]
        back_raw = bgr[top_dash_y + 4 : bot_dash_y - 4, center_x + 4 : right_x - 4]
        return {
            "method": "ration_wb_precision_frame",
            "front": front_raw,
            "back": back_raw,
            "no_trim": True
        }

    # -------------------------------------------------------------------------
    # STRATEGY 1B: AYUSHMAN BHARAT / ABHA HEALTH CARD (Vertical Stack Engine)
    # -------------------------------------------------------------------------
    if is_ayushman:
        # ABHA cards on 1-page documents are stacked vertically: Front on top, Back on bottom
        gray = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)
        edges = cv2.Canny(gray, 30, 120)
        kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (15, 15))
        closed = cv2.morphologyEx(edges, cv2.MORPH_CLOSE, kernel)
        contours, _ = cv2.findContours(closed, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)

        abha_boxes = []
        for c in contours:
            bx, by, bw, bh = cv2.boundingRect(c)
            area = bw * bh
            ar = bw / float(bh) if bh > 0 else 0
            if area > (w * h * 0.10) and 1.25 <= ar <= 1.95:
                abha_boxes.append((bx, by, bw, bh, ar, area))

        if len(abha_boxes) >= 2:
            abha_boxes.sort(key=lambda b: b[1])  # Sort vertically (top to bottom)
            c0 = abha_boxes[0]
            c1 = abha_boxes[1]
            if (c0[1] + c0[3]) <= (c1[1] + 0.15 * c1[3]):
                f = bgr[c0[1]:c0[1] + c0[3], c0[0]:c0[0] + c0[2]]
                b = bgr[c1[1]:c1[1] + c1[3], c1[0]:c1[0] + c1[2]]
                return {
                    "method": "abha_vertical_stack",
                    "front": f,
                    "back": b,
                    "no_trim": True
                }
        elif ar_page < 1.15:
            # Fallback for portrait 1-page ABHA cards: split at middle
            mid_y = h // 2
            card_h = int(mid_y * 0.96)
            card_w = int(w * 0.96)
            min_x = int(w * 0.02)
            f = bgr[int(h * 0.015):int(h * 0.015) + card_h, min_x:min_x + card_w]
            b = bgr[mid_y + int(h * 0.005):mid_y + int(h * 0.005) + card_h, min_x:min_x + card_w]
            return {
                "method": "abha_vertical_split_fallback",
                "front": f,
                "back": b
            }

    # -------------------------------------------------------------------------
    # STRATEGY 2: UNIVERSAL DIRECTIONAL MORPHOLOGICAL EDGE FUSION
    # Bridges dashed scissor cut lines and card borders into closed contours
    # -------------------------------------------------------------------------
    gray = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)
    edges = cv2.Canny(gray, 30, 120)

    # Adaptive kernel sizing proportional to image resolution
    kw = max(21, int(w * 0.012)) | 1
    kh = max(21, int(h * 0.012)) | 1
    k_h = cv2.getStructuringElement(cv2.MORPH_RECT, (kw, 3))
    k_v = cv2.getStructuringElement(cv2.MORPH_RECT, (3, kh))

    closed = cv2.morphologyEx(edges, cv2.MORPH_CLOSE, k_h)
    closed = cv2.morphologyEx(closed, cv2.MORPH_CLOSE, k_v)

    contours, _ = cv2.findContours(closed, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)

    single_candidates = []
    double_candidates = []

    for c in contours:
        bx, by, bw, bh = cv2.boundingRect(c)
        area = bw * bh
        ar = bw / float(bh) if bh > 0 else 0
        area_pct = area / float(w * h)

        # Single Card Candidate: 1.25 <= ar <= 1.95, area between 1.5% and 40% of page
        if 0.015 <= area_pct <= 0.40 and 1.25 <= ar <= 1.95:
            single_candidates.append((bx, by, bw, bh, ar, area, area_pct))

        # Double Card Block (Side-by-side Front+Back): 1.90 <= ar <= 3.8, area between 3% and 80%
        if 0.03 <= area_pct <= 0.80 and 1.90 <= ar <= 3.8:
            double_candidates.append((bx, by, bw, bh, ar, area, area_pct))

    # 2A. Check for Twin ID Cards (Side-by-side or Stacked Front & Back, e.g. Voter ID e-EPIC or ABHA)
    if len(single_candidates) >= 2:
        single_candidates.sort(key=lambda b: b[5], reverse=True)
        for i in range(len(single_candidates)):
            for j in range(i + 1, len(single_candidates)):
                b1 = single_candidates[i]
                b2 = single_candidates[j]
                max_h = max(b1[3], b2[3])
                max_w = max(b1[2], b2[2])

                # Case 1: Side-by-side Twin Cards (Voter ID, horizontal layout)
                if abs(b1[1] - b2[1]) <= 0.12 * max_h and abs(b1[3] - b2[3]) <= 0.18 * max_h:
                    left_box = b1 if b1[0] < b2[0] else b2
                    right_box = b2 if b1[0] < b2[0] else b1
                    if (left_box[0] + left_box[2]) <= (right_box[0] + 0.15 * right_box[2]):
                        f = bgr[left_box[1]:left_box[1] + left_box[3], left_box[0]:left_box[0] + left_box[2]]
                        b = bgr[right_box[1]:right_box[1] + right_box[3], right_box[0]:right_box[0] + right_box[2]]
                        return {
                            "method": "twin_cards_morphology",
                            "front": f,
                            "back": b
                        }

                # Case 2: Vertically Stacked Twin Cards (ABHA / Ayushman, vertical layout)
                if abs(b1[0] - b2[0]) <= 0.15 * max_w and abs(b1[2] - b2[2]) <= 0.18 * max_w:
                    top_box = b1 if b1[1] < b2[1] else b2
                    bot_box = b2 if b1[1] < b2[1] else b1
                    if (top_box[1] + top_box[3]) <= (bot_box[1] + 0.15 * bot_box[3]):
                        f = bgr[top_box[1]:top_box[1] + top_box[3], top_box[0]:top_box[0] + top_box[2]]
                        b = bgr[bot_box[1]:bot_box[1] + bot_box[3], bot_box[0]:bot_box[0] + bot_box[2]]
                        return {
                            "method": "vertical_twin_cards_morphology",
                            "front": f,
                            "back": b
                        }

    # 2B. Check for UIDAI Aadhaar baseline anchor (if recognized as Aadhaar)
    if is_aadhaar or "aadhaar" in doc_type_upper:
        y_offset = int(0.55 * h)
        roi = bgr[y_offset:, :]
        roi_h, roi_w = roi.shape[:2]
        roi_gray = cv2.cvtColor(roi, cv2.COLOR_BGR2GRAY)

        bot_y = None
        for y in range(roi_h - 1, int(0.60 * roi_h), -1):
            if np.sum(roi_gray[y, :] < 50) > int(roi_w * 0.45):
                bot_y = y
                break

        if bot_y is not None:
            card_w = int(roi_w * 0.43)
            card_h = int(card_w / 1.58)
            top_y = max(0, bot_y - card_h)

            center_x = roi_w // 2
            mid_slice = roi_gray[top_y:bot_y, int(0.46 * roi_w):int(0.54 * roi_w)]
            if mid_slice.size > 0:
                col_std = np.std(mid_slice, axis=0)
                center_x = int(0.46 * roi_w) + int(np.argmin(col_std))

            front_raw = roi[top_y:bot_y + 2, int(0.06 * roi_w):center_x - 10]
            back_raw = roi[top_y:bot_y + 2, center_x + 15:int(0.94 * roi_w)]
            return {
                "method": "aadhaar_vision_anchor",
                "front": front_raw,
                "back": back_raw
            }

    # 2C. Check for Single Card (e.g. PAN Card, ABHA Card, Driving License)
    # If the document is PAN or only 1 prominent ID-sized rectangle exists
    if is_pan or (len(single_candidates) >= 1 and len(double_candidates) == 0):
        if single_candidates:
            single_candidates.sort(key=lambda b: b[5], reverse=True)
            top = single_candidates[0]
            f = bgr[top[1]:top[1] + top[3], top[0]:top[0] + top[2]]
            return {
                "method": "single_card_morphology",
                "front": f,
                "back": None
            }

    # 2D. Check for Double-Card Block (e.g. Aadhaar bottom block or joined cards)
    if double_candidates:
        double_candidates.sort(key=lambda b: b[5], reverse=True)
        top = double_candidates[0]
        block = bgr[top[1]:top[1] + top[3], top[0]:top[0] + top[2]]
        bh, bw = block.shape[:2]

        # Find middle seam between left and right card
        mid_s = int(bw * 0.44)
        mid_e = int(bw * 0.56)
        block_gray = cv2.cvtColor(block, cv2.COLOR_BGR2GRAY)
        col_means = [np.mean(block_gray[:, x]) for x in range(mid_s, mid_e)]
        split_x = mid_s + int(np.argmin(col_means)) if col_means else (bw // 2)

        f_half = block[:, :split_x]
        b_half = block[:, split_x:]

        # If aspect ratio of each half is < 1.30, it has attached letterhead (e.g. Aadhaar letter)
        # In official Aadhaar format, the physical card is situated at the bottom
        f_ar = f_half.shape[1] / float(f_half.shape[0]) if f_half.shape[0] > 0 else 1.0
        if f_ar < 1.30:
            target_h = int(f_half.shape[1] / 1.58)
            if target_h < f_half.shape[0]:
                f_half = f_half[f_half.shape[0] - target_h:, :]
                b_half = b_half[b_half.shape[0] - target_h:, :]

        f_half = trim_and_clean_card_edges(f_half)
        b_half = trim_and_clean_card_edges(b_half)

        return {
            "method": "double_block_split",
            "front": f_half,
            "back": b_half
        }

    # 2E. If single candidate exists without being PAN, return it
    if single_candidates:
        single_candidates.sort(key=lambda b: b[5], reverse=True)
        top = single_candidates[0]
        f = bgr[top[1]:top[1] + top[3], top[0]:top[0] + top[2]]
        return {
            "method": "single_card_morphology",
            "front": f,
            "back": None
        }

    # -------------------------------------------------------------------------
    # STRATEGY 3: CALIBRATED ADAPTIVE FALLBACK (Bottom Region of Page)
    # -------------------------------------------------------------------------
    card_crop = bgr[int(h * 0.65):int(h * 0.98), int(w * 0.04):int(w * 0.96)]
    cw_ar = card_crop.shape[1] / float(card_crop.shape[0]) if card_crop.shape[0] > 0 else 1.0

    if cw_ar > 1.80:
        split_x = card_crop.shape[1] // 2
        f = card_crop[:, :split_x]
        b = card_crop[:, split_x:]
        return {
            "method": "adaptive_split_fallback",
            "front": f,
            "back": b
        }

    return {
        "method": "calibrated_fallback",
        "front": card_crop,
        "back": None
    }


def extract_cards_from_page_vision(page, pno, out_dir, doc_type="auto", auto_whiten=True, dpi=300):
    """Renders a PDF page and extracts crisp, unclipped ID card(s)."""
    pw, ph = page.rect.width, page.rect.height
    ar_page = pw / float(ph) if ph > 0 else 1.0

    # Page is already an isolated ID card
    if ph < 450 or ar_page >= 1.30:
        pix = page.get_pixmap(dpi=dpi)
        img = np.frombuffer(pix.samples, dtype=np.uint8).reshape((pix.height, pix.width, pix.n))
        bgr = cv2.cvtColor(img, cv2.COLOR_RGB2BGR) if (cv2 and pix.n == 3) else img
        front = finalize_card(bgr, auto_whiten)
        tag = f"{os.getpid()}_{pno}_{random.randint(1000, 9999)}"
        front_path = os.path.join(out_dir, f"card_front_{tag}.png")
        cv2.imwrite(front_path, front)
        return {
            "success": True,
            "front_image": os.path.abspath(front_path),
            "back_image": "",
            "has_back": False,
            "width": front.shape[1],
            "height": front.shape[0],
            "aspect_ratio": round(front.shape[1] / float(front.shape[0]), 2),
            "method": "pre_cropped_card"
        }

    # Render entire page at high resolution
    pix = page.get_pixmap(dpi=dpi)
    img = np.frombuffer(pix.samples, dtype=np.uint8).reshape((pix.height, pix.width, pix.n))
    bgr = cv2.cvtColor(img, cv2.COLOR_RGB2BGR) if (cv2 and pix.n == 3) else img

    text_blocks = page.get_text("blocks")
    full_text = " ".join([b[4] for b in text_blocks]).lower()

    det = detect_cards_universal(bgr, full_text=full_text, doc_type=doc_type)
    if not det or det.get("front") is None:
        return None

    tag = f"{os.getpid()}_{pno}_{random.randint(1000, 9999)}"
    os.makedirs(out_dir, exist_ok=True)

    no_trim = det.get("no_trim", False)
    front = finalize_card(det["front"], auto_whiten, trim_edges=(not no_trim))
    front_path = os.path.join(out_dir, f"card_front_{tag}.png")
    cv2.imwrite(front_path, front)

    has_back = det.get("back") is not None and det["back"].size > 0
    back_path = ""
    if has_back:
        back = finalize_card(det["back"], auto_whiten, trim_edges=(not no_trim))
        back_path = os.path.join(out_dir, f"card_back_{tag}.png")
        cv2.imwrite(back_path, back)

    return {
        "success": True,
        "front_image": os.path.abspath(front_path),
        "back_image": os.path.abspath(back_path) if has_back else "",
        "has_back": has_back,
        "width": front.shape[1],
        "height": front.shape[0],
        "aspect_ratio": round(front.shape[1] / float(front.shape[0]), 2),
        "method": det.get("method", "universal_vision")
    }


def process_pdf_document(pdf_path, out_dir, page_num=-1, doc_type="auto", auto_whiten=True, dpi=300):
    """Processes PDF document, returning either a single card or list of cards for multi-page batches."""
    if fitz is None:
        return None

    doc = fitz.open(pdf_path)
    total_pages = len(doc)
    if total_pages == 0:
        return None

    # Check for 2-page Ayushman Bharat / ABHA Card (Page 0 = Front, Page 1 = Back)
    p0 = doc[0]
    p0_ar = p0.rect.width / float(p0.rect.height) if p0.rect.height > 0 else 1.0
    is_card_sized = p0.rect.height < 380 or p0_ar >= 1.35
    is_ayushman_doc = total_pages == 2 and (is_card_sized or "AYUSHMAN" in doc_type.upper() or "PMJAY" in doc_type.upper())

    if is_ayushman_doc:
        pix0 = doc[0].get_pixmap(dpi=dpi)
        pix1 = doc[1].get_pixmap(dpi=dpi)

        img0 = np.frombuffer(pix0.samples, dtype=np.uint8).reshape((pix0.height, pix0.width, pix0.n))
        bgr0 = cv2.cvtColor(img0, cv2.COLOR_RGB2BGR) if (cv2 and pix0.n == 3) else img0

        img1 = np.frombuffer(pix1.samples, dtype=np.uint8).reshape((pix1.height, pix1.width, pix1.n))
        front = auto_whiten_image(bgr0) if auto_whiten else bgr0
        back = auto_whiten_image(bgr1) if auto_whiten else bgr1
        front = add_subtle_card_border(front)
        back = add_subtle_card_border(back)

        os.makedirs(out_dir, exist_ok=True)
        tag = f"{os.getpid()}_{random.randint(1000, 9999)}"
        front_path = os.path.join(out_dir, f"card_front_{tag}.png")
        back_path = os.path.join(out_dir, f"card_back_{tag}.png")

        cv2.imwrite(front_path, front)
        cv2.imwrite(back_path, back)

        return {
            "success": True,
            "front_image": os.path.abspath(front_path),
            "back_image": os.path.abspath(back_path),
            "has_back": True,
            "width": front.shape[1],
            "height": front.shape[0],
            "aspect_ratio": round(front.shape[1] / float(front.shape[0]), 2),
            "method": "ayushman_2page_doc"
        }

    # If specific page requested (0-indexed)
    if 0 <= page_num < total_pages:
        return extract_cards_from_page_vision(doc[page_num], page_num, out_dir, doc_type, auto_whiten, dpi)

    # Process all pages (handles 1, 5, 10, 43, 100+ pages seamlessly)
    cards = []
    for p in range(total_pages):
        card_res = extract_cards_from_page_vision(doc[p], p, out_dir, doc_type, auto_whiten, dpi)
        if card_res and card_res.get("success"):
            card_res["page_number"] = p + 1
            cards.append(card_res)

    if len(cards) == 1:
        return cards[0]
    elif len(cards) > 1:
        return {
            "success": True,
            "cards": cards,
            "total_cards": len(cards)
        }

    return None


def detect_card_in_photo(bgr):
    """
    On-device smart card detector for smartphone camera photos, Phone Link, and WhatsApp images.
    Detects high-contrast rectangular cards (Aadhaar, PAN, Voter, Ration) lying on tables or held in hands.
    Automatically rotates vertical portrait photos to standard landscape.
    """
    if cv2 is None or bgr is None or bgr.size == 0:
        return None

    h, w = bgr.shape[:2]
    max_dim = max(h, w)
    scale = 1200.0 / max_dim if max_dim > 1200 else 1.0
    proc = cv2.resize(bgr, (0, 0), fx=scale, fy=scale) if scale != 1.0 else bgr.copy()
    ph, pw = proc.shape[:2]

    gray = cv2.cvtColor(proc, cv2.COLOR_BGR2GRAY)
    blurred = cv2.GaussianBlur(gray, (5, 5), 0)

    # Combine Canny edges with Otsu thresholding edges
    edges = cv2.Canny(blurred, 35, 120)
    _, thresh = cv2.threshold(blurred, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    thresh_edges = cv2.Canny(thresh, 50, 150)
    combined = cv2.bitwise_or(edges, thresh_edges)

    kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (7, 7))
    closed = cv2.morphologyEx(combined, cv2.MORPH_CLOSE, kernel)

    contours, _ = cv2.findContours(closed, cv2.RETR_TREE, cv2.CHAIN_APPROX_SIMPLE)

    candidates = []
    total_area = pw * ph

    for c in contours:
        area = cv2.contourArea(c)
        if area < (total_area * 0.05):  # At least 5% of the photo
            continue

        bx, by, bw, bh = cv2.boundingRect(c)
        box_area = bw * bh
        if box_area < (total_area * 0.06):
            continue

        rect_fill = area / float(box_area) if box_area > 0 else 0
        if rect_fill < 0.55:  # Reasonably rectangular
            continue

        ar = bw / float(bh) if bh > 0 else 0
        is_landscape_card = 1.15 <= ar <= 2.25
        is_portrait_card = 0.44 <= ar <= 0.88

        if not (is_landscape_card or is_portrait_card):
            continue

        peri = cv2.arcLength(c, True)
        approx = cv2.approxPolyDP(c, 0.025 * peri, True)

        score = area * rect_fill
        if len(approx) == 4:
            score *= 1.4
        if is_landscape_card and (1.40 <= ar <= 1.75):
            score *= 1.8
        elif is_portrait_card and (0.55 <= ar <= 0.72):
            score *= 1.8

        candidates.append({
            "bx": bx, "by": by, "bw": bw, "bh": bh,
            "ar": ar, "score": score,
            "is_portrait": is_portrait_card,
            "area_pct": box_area / float(total_area)
        })

    if candidates:
        candidates.sort(key=lambda x: x["score"], reverse=True)
        best = candidates[0]

        inv = 1.0 / scale
        rx = int(best["bx"] * inv)
        ry = int(best["by"] * inv)
        rw = int(best["bw"] * inv)
        rh = int(best["bh"] * inv)

        rx = max(0, min(w - 1, rx))
        ry = max(0, min(h - 1, ry))
        rw = max(10, min(w - rx, rw))
        rh = max(10, min(h - ry, rh))

        crop = bgr[ry:ry + rh, rx:rx + rw]

        crop, rotated = auto_rotate_to_landscape(crop)

        return {
            "crop": crop,
            "rect": [rx, ry, rw, rh],
            "rotated": rotated,
            "aspect_ratio": round(crop.shape[1] / float(crop.shape[0]), 2)
        }

    return None


def auto_rotate_to_landscape(card_bgr):
    """
    Rotates a vertical card (height > width) to standard horizontal landscape (width > height),
    using Aadhaar/PAN/Voter text and emblem cues to pick between 90 deg CCW and 90 deg CW.
    """
    if card_bgr is None or card_bgr.shape[0] <= card_bgr.shape[1]:
        return card_bgr, False

    rot_ccw = cv2.rotate(card_bgr, cv2.ROTATE_90_COUNTERCLOCKWISE)
    rot_cw = cv2.rotate(card_bgr, cv2.ROTATE_90_CLOCKWISE)

    # In Aadhaar card back, red/orange Aadhaar emblem & 'मेरा आधार' strip is at the BOTTOM.
    # In Aadhaar card front, 'भारत सरकार / GOVERNMENT OF INDIA' header is at the TOP.
    hsv_ccw = cv2.cvtColor(rot_ccw, cv2.COLOR_BGR2HSV)
    hsv_cw = cv2.cvtColor(rot_cw, cv2.COLOR_BGR2HSV)

    # Red/orange strip detection (Hue in 0-14 or 165-180, Sat > 70, Val > 60)
    red_ccw = ((hsv_ccw[:, :, 0] < 14) | (hsv_ccw[:, :, 0] > 165)) & (hsv_ccw[:, :, 1] > 70) & (hsv_ccw[:, :, 2] > 60)
    red_cw = ((hsv_cw[:, :, 0] < 14) | (hsv_cw[:, :, 0] > 165)) & (hsv_cw[:, :, 1] > 70) & (hsv_cw[:, :, 2] > 60)

    h_ccw = rot_ccw.shape[0]
    red_lower_ccw = np.sum(red_ccw[int(h_ccw * 0.5):, :])
    red_lower_cw = np.sum(red_cw[int(h_ccw * 0.5):, :])

    if red_lower_ccw > (red_lower_cw * 1.5):
        return rot_ccw, True
    elif red_lower_cw > (red_lower_ccw * 1.5):
        return rot_cw, True

    # Default for portrait smartphone photos: 90 deg CCW
    return rot_ccw, True


def find_dense_card_region(img):
    """
    Detects high-density printed card content on photocopied / scanned white sheets.
    Identifies the ID card boundaries by analyzing row/column variance on plain paper.
    """
    if img is None or img.size == 0 or cv2 is None or np is None:
        return None
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    row_std = np.std(gray, axis=1)
    col_std = np.std(gray, axis=0)
    thresh_r = max(14.0, float(np.percentile(row_std, 35)))
    thresh_c = max(14.0, float(np.percentile(col_std, 35)))
    active_y = np.where(row_std > thresh_r)[0]
    active_x = np.where(col_std > thresh_c)[0]
    if len(active_y) > 0 and len(active_x) > 0:
        pad = 8
        y1 = max(0, int(active_y[0] - pad))
        y2 = min(img.shape[0], int(active_y[-1] + pad))
        x1 = max(0, int(active_x[0] - pad))
        x2 = min(img.shape[1], int(active_x[-1] + pad))
        w = x2 - x1
        h = y2 - y1
        if w > 40 and h > 40:
            ar = w / float(h)
            if (1.15 <= ar <= 2.25) or (0.45 <= ar <= 0.88):
                return [x1, y1, w, h]
    return None


def fallback_photo_card_detect(img):
    """
    Robust fallback detector for smartphone camera photos where hands/fingers break simple contour rectangles.
    Finds bright card paper and high-contrast text extent, then auto-rotates to landscape.
    """
    if img is None or img.size == 0:
        return None
    h, w = img.shape[:2]

    # Check for dense card region first (e.g. photocopied / scanned card on white sheet)
    dense_rect = find_dense_card_region(img)
    if dense_rect is not None:
        rx, ry, rw, rh = dense_rect
        crop = img[ry:ry + rh, rx:rx + rw]
        crop, rotated = auto_rotate_to_landscape(crop)
        return {
            "crop": crop,
            "rect": [rx, ry, rw, rh],
            "rotated": rotated,
            "aspect_ratio": round(crop.shape[1] / float(crop.shape[0]), 2),
            "method": "dense_card_region"
        }

    scale = 800.0 / max(h, w)
    small = cv2.resize(img, (0, 0), fx=scale, fy=scale)
    sh, sw = small.shape[:2]
    gray = cv2.cvtColor(small, cv2.COLOR_BGR2GRAY)
    hsv = cv2.cvtColor(small, cv2.COLOR_BGR2HSV)

    is_card_color = (hsv[:, :, 1] < 80) & (hsv[:, :, 2] > 110) & (gray > 110)
    card_mask = (is_card_color * 255).astype(np.uint8)
    kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (max(5, int(sw * 0.05)), max(5, int(sh * 0.05))))
    closed = cv2.morphologyEx(card_mask, cv2.MORPH_CLOSE, kernel)

    contours, _ = cv2.findContours(closed, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    best_rect = None
    best_area = 0
    for c in contours:
        area = cv2.contourArea(c)
        if area > (sw * sh * 0.15):
            bx, by, bw, bh = cv2.boundingRect(c)
            if area > best_area:
                best_area = area
                inv = 1.0 / scale
                best_rect = [int(bx * inv), int(by * inv), int(bw * inv), int(bh * inv)]

    if best_rect is None:
        best_rect = [0, 0, w, h]

    rx, ry, rw, rh = best_rect
    rx = max(0, min(w - 1, rx))
    ry = max(0, min(h - 1, ry))
    rw = max(10, min(w - rx, rw))
    rh = max(10, min(h - ry, rh))

    crop = img[ry:ry + rh, rx:rx + rw]
    crop, rotated = auto_rotate_to_landscape(crop)
    return {
        "crop": crop,
        "rect": [rx, ry, rw, rh],
        "rotated": rotated,
        "aspect_ratio": round(crop.shape[1] / float(crop.shape[0]), 2),
        "method": "photo_bright_segmentation"
    }


def main():
    parser = argparse.ArgumentParser(description="DASMO Govt Card Extractor Engine")
    parser.add_argument("--input", required=True, help="Path to input PDF or Image file")
    parser.add_argument("--outdir", required=True, help="Directory to save extracted cards")
    parser.add_argument("--page", type=int, default=-1, help="Specific page number (0-indexed, default -1 for all)")
    parser.add_argument("--doctype", default="auto", help="Document Type preset")
    parser.add_argument("--autowhiten", type=lambda v: str(v).lower() in ("true", "1", "yes"), default=True)
    parser.add_argument("--dpi", type=int, default=300)
    parser.add_argument("--toptrim", type=float, default=0.0)
    parser.add_argument("--bottrim", type=float, default=0.0)
    parser.add_argument("--mode", default="auto", help="Mode: 'auto' or 'autocrop'")

    args = parser.parse_args()
    input_path = os.path.abspath(args.input)
    out_dir = os.path.abspath(args.outdir)

    if not os.path.exists(input_path):
        print(json.dumps({"success": False, "error": f"Input file not found: {input_path}"}))
        sys.exit(1)

    try:
        ext = os.path.splitext(input_path)[1].lower()
        result = None

        if ext == ".pdf":
            result = process_pdf_document(input_path, out_dir, args.page, args.doctype, args.autowhiten, args.dpi)
        elif ext in (".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tiff"):
            img = cv2.imread(input_path) if cv2 else None
            if img is not None:
                os.makedirs(out_dir, exist_ok=True)
                tag = f"{os.getpid()}_img_{random.randint(1000, 9999)}"

                if args.mode == "autocrop":
                    photo_det = detect_card_in_photo(img)
                    if photo_det is None:
                        photo_det = fallback_photo_card_detect(img)

                    if photo_det is not None:
                        front = finalize_card(photo_det["crop"], args.autowhiten)
                        # Guarantee landscape orientation
                        if front.shape[0] > front.shape[1]:
                            front, _ = auto_rotate_to_landscape(front)

                        front_path = os.path.join(out_dir, f"card_front_{tag}.png")
                        cv2.imwrite(front_path, front)

                        result = {
                            "success": True,
                            "Success": True,
                            "front_image": os.path.abspath(front_path),
                            "FrontImage": os.path.abspath(front_path),
                            "back_image": "",
                            "BackImage": "",
                            "has_back": False,
                            "HasBack": False,
                            "crop_rect": photo_det["rect"],
                            "CropRect": photo_det["rect"],
                            "rotated": photo_det["rotated"],
                            "Rotated": photo_det["rotated"],
                            "width": front.shape[1],
                            "height": front.shape[0],
                            "aspect_ratio": round(front.shape[1] / float(front.shape[0]), 2),
                            "method": photo_det.get("method", "photo_detector")
                        }
                else:
                    # Check if photo detection should be tried
                    photo_det = detect_card_in_photo(img)
                    if photo_det is not None:
                        front = finalize_card(photo_det["crop"], args.autowhiten)
                        if front.shape[0] > front.shape[1]:
                            front, _ = auto_rotate_to_landscape(front)

                        front_path = os.path.join(out_dir, f"card_front_{tag}.png")
                        cv2.imwrite(front_path, front)

                        result = {
                            "success": True,
                            "Success": True,
                            "front_image": os.path.abspath(front_path),
                            "FrontImage": os.path.abspath(front_path),
                            "back_image": "",
                            "BackImage": "",
                            "has_back": False,
                            "HasBack": False,
                            "crop_rect": photo_det["rect"],
                            "CropRect": photo_det["rect"],
                            "rotated": photo_det["rotated"],
                            "Rotated": photo_det["rotated"],
                            "width": front.shape[1],
                            "height": front.shape[0],
                            "aspect_ratio": photo_det["aspect_ratio"],
                            "method": "photo_contour_detector"
                        }
                    else:
                        # Run standard universal detection
                        det = detect_cards_universal(img, full_text="", doc_type=args.doctype)
                        if det and det.get("front") is not None:
                            no_trim = det.get("no_trim", False)
                            front = finalize_card(det["front"], args.autowhiten, trim_edges=(not no_trim))
                            front_path = os.path.join(out_dir, f"card_front_{tag}.png")
                            cv2.imwrite(front_path, front)

                            has_back = det.get("back") is not None and det["back"].size > 0
                            back_path = ""
                            if has_back:
                                back = finalize_card(det["back"], args.autowhiten, trim_edges=(not no_trim))
                                back_path = os.path.join(out_dir, f"card_back_{tag}.png")
                                cv2.imwrite(back_path, back)

                            result = {
                                "success": True,
                                "Success": True,
                                "front_image": os.path.abspath(front_path),
                                "FrontImage": os.path.abspath(front_path),
                                "back_image": os.path.abspath(back_path) if has_back else "",
                                "BackImage": os.path.abspath(back_path) if has_back else "",
                                "has_back": has_back,
                                "HasBack": has_back,
                                "width": front.shape[1],
                                "height": front.shape[0],
                                "aspect_ratio": round(front.shape[1] / float(front.shape[0]), 2),
                                "method": det.get("method", "image_universal_cv")
                            }

        if result and result.get("success"):
            print(json.dumps(result))
            sys.exit(0)
        else:
            print(json.dumps({"success": False, "error": "Could not detect card region."}))
            sys.exit(2)

    except Exception as ex:
        err_msg = f"{type(ex).__name__}: {str(ex)}\n{traceback.format_exc()}"
        print(json.dumps({"success": False, "error": err_msg}))
        sys.exit(3)


if __name__ == "__main__":
    main()
