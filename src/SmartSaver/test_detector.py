import cv2
import numpy as np
from PIL import Image
import os
import sys

def detect_govt_card_bounds(image_path):
    print(f"Testing detection on: {image_path}")
    img = cv2.imread(image_path)
    if img is None:
        print("Error: Failed to read image")
        return None

    h, w = img.shape[:2]
    print(f"Image dimensions: {w}x{h}")

    # Convert to grayscale and HSV
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    hsv = cv2.cvtColor(img, cv2.COLOR_BGR2HSV)

    # In Indian govt documents (Ration Card, Aadhaar, PAN, Voter),
    # the cards are usually located in the bottom 60% of the document (y > 0.4 * h)
    roi_ymin = int(0.40 * h)
    roi_img = img[roi_ymin:h, 0:w]
    roi_gray = gray[roi_ymin:h, 0:w]
    roi_hsv = hsv[roi_ymin:h, 0:w]

    # Strategy 1: Color Mask (e.g. WB Ration Card has distinct blue/cyan, Aadhaar has saffron/green, PAN has light blue)
    # Saturation > 25 and Value > 50 (colored card background)
    sat = roi_hsv[:, :, 1]
    val = roi_hsv[:, :, 2]
    color_mask = ((sat > 30) & (val > 80)).astype(np.uint8) * 255

    # Strategy 2: Edge detection & dashed border contours
    blurred = cv2.GaussianBlur(roi_gray, (5, 5), 0)
    edges = cv2.Canny(blurred, 30, 120)

    # Morphological closing to connect dashed scissor lines / card borders
    kernel_h = cv2.getStructuringElement(cv2.MORPH_RECT, (25, 3))
    kernel_v = cv2.getStructuringElement(cv2.MORPH_RECT, (3, 25))
    closed_h = cv2.morphologyEx(edges, cv2.MORPH_CLOSE, kernel_h)
    closed_v = cv2.morphologyEx(edges, cv2.MORPH_CLOSE, kernel_v)
    combined_edges = cv2.bitwise_or(closed_h, closed_v)
    combined_edges = cv2.bitwise_or(combined_edges, color_mask)

    # Connect components into full card rectangle
    connect_kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (15, 15))
    connected = cv2.morphologyEx(combined_edges, cv2.MORPH_CLOSE, connect_kernel)

    # Find contours
    contours, _ = cv2.findContours(connected, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)

    candidates = []
    for c in contours:
        x, y, cw, ch = cv2.boundingRect(c)
        actual_y = roi_ymin + y
        area = cw * ch
        aspect_ratio = cw / float(ch) if ch > 0 else 0

        # We look for card boxes:
        # A single card has aspect ratio ~ 1.58 (1.3 to 1.8)
        # A double card (Front + Back side by side) has aspect ratio ~ 3.16 (2.2 to 3.8)
        # Area should be significant (e.g. at least 5% of page width and 5% of page height)
        if cw > 0.3 * w and ch > 0.1 * h:
            candidates.append((x, actual_y, cw, ch, area, aspect_ratio))
            print(f"Candidate: x={x}, y={actual_y}, w={cw}, h={ch}, AR={aspect_ratio:.2f}, Area={area}")

    if not candidates:
        print("No candidates found via morphology, testing fallback gradient/projection...")
        # Fallback: Horizontal projection of colored/edge pixels in bottom half
        row_sums = np.sum(color_mask > 0, axis=1)
        active_rows = np.where(row_sums > 0.15 * w)[0]
        if len(active_rows) > 0:
            ymin_rel = active_rows[0]
            ymax_rel = active_rows[-1]
            card_y = roi_ymin + ymin_rel
            card_h = ymax_rel - ymin_rel
            # Horizontal span
            col_sums = np.sum(color_mask[ymin_rel:ymax_rel, :] > 0, axis=0)
            active_cols = np.where(col_sums > 0.10 * card_h)[0]
            if len(active_cols) > 0:
                card_x = active_cols[0]
                card_w = active_cols[-1] - card_x
                candidates.append((card_x, card_y, card_w, card_h, card_w * card_h, card_w / float(card_h)))
                print(f"Fallback Projection Candidate: x={card_x}, y={card_y}, w={card_w}, h={card_h}")

    if candidates:
        # Pick the lowest card block with standard aspect ratio (~2.4 to 3.5 for double card, or ~1.4 to 1.8 for single card)
        candidates.sort(key=lambda item: (item[1], item[4]), reverse=True)
        best = candidates[0]
        bx, by, bw, bh = best[0], best[1], best[2], best[3]
        print(f"Selected BEST Card Region: x={bx}, y={by}, w={bw}, h={bh}")

        # Crop the total card region
        card_crop = img[by:by+bh, bx:bx+bw]
        out_dir = os.path.dirname(image_path)
        cv2.imwrite(os.path.join(out_dir, "test_detected_full_card.png"), card_crop)

        # If it's a double card (AR > 2.0), split into Front (left) and Back (right)
        if (bw / float(bh)) > 2.0:
            mid_x = bw // 2
            front_crop = card_crop[:, 0:mid_x]
            back_crop = card_crop[:, mid_x:bw]
            cv2.imwrite(os.path.join(out_dir, "test_detected_front.png"), front_crop)
            cv2.imwrite(os.path.join(out_dir, "test_detected_back.png"), back_crop)
            print("Successfully saved Front and Back cards!")
            return True

    return False

if __name__ == "__main__":
    img_path = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Mypc3\.gemini\antigravity\brain\8d4b03a4-fd55-402d-af30-656b997e2221\.user_uploaded\media_1788009337899.png"
    detect_govt_card_bounds(img_path)
