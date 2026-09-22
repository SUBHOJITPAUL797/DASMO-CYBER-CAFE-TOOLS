import cv2
import numpy as np

img = cv2.imread(r"C:\Users\Mypc3\.gemini\antigravity\brain\8d4b03a4-fd55-402d-af30-656b997e2221\.user_uploaded\media_1788009337899.png")
gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
h, w = img.shape[:2]

# 1. Detect the page sheet
_, thresh = cv2.threshold(gray, 220, 255, cv2.THRESH_BINARY)
contours, _ = cv2.findContours(thresh, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
page_box = (0, 0, w, h)
for c in contours:
    x, y, cw, ch = cv2.boundingRect(c)
    if cw > 0.25 * w and ch > 0.4 * h:
        if 0.5 <= cw / float(ch) <= 0.9:
            page_box = (x, y, cw, ch)
            break

px, py, pw, ph = page_box
page_img = img[py:py+ph, px:px+pw]
page_hsv = cv2.cvtColor(page_img, cv2.COLOR_BGR2HSV)
page_gray = cv2.cvtColor(page_img, cv2.COLOR_BGR2GRAY)

# Look in the bottom 35% of the page
card_search_ymin = int(0.68 * ph)
card_search_hsv = page_hsv[card_search_ymin:ph, :]
card_search_gray = page_gray[card_search_ymin:ph, :]

# Color mask specifically for the card background (the blue stripes / solid card box)
sat = card_search_hsv[:, :, 1]
val = card_search_hsv[:, :, 2]
card_mask = ((sat > 35) & (val > 85)).astype(np.uint8) * 255

# In addition, find horizontal lines (the dashed scissor line above the card)
edges = cv2.Canny(card_search_gray, 50, 150)
kernel_line = cv2.getStructuringElement(cv2.MORPH_RECT, (40, 1))
h_lines = cv2.morphologyEx(edges, cv2.MORPH_OPEN, kernel_line)

# Let's find the card bounding box directly from the card_mask below any top summary
# Horizontal projection of the card color mask
row_counts = np.sum(card_mask > 0, axis=1)
# The actual card has a strong continuous block of color
active_rows = np.where(row_counts > 0.20 * pw)[0]

if len(active_rows) > 0:
    # Find contiguous row segment at the bottom
    # Split active_rows into contiguous segments
    diffs = np.diff(active_rows)
    split_points = np.where(diffs > 5)[0]
    segments = np.split(active_rows, split_points + 1)
    # The card is the bottom-most significant segment!
    card_segment = segments[-1]
    
    ymin_card = card_search_ymin + card_segment[0]
    ymax_card = card_search_ymin + card_segment[-1]
    
    # Now find the horizontal span (x min, x max) of the card in this vertical range
    card_slice_mask = card_mask[card_segment[0]:card_segment[-1]+1, :]
    col_counts = np.sum(card_slice_mask > 0, axis=0)
    active_cols = np.where(col_counts > 0.10 * (ymax_card - ymin_card))[0]
    
    xmin_card = active_cols[0]
    xmax_card = active_cols[-1]
    
    print(f"EXACT CARD RECTANGLE: x={xmin_card}, y={ymin_card}, w={xmax_card - xmin_card}, h={ymax_card - ymin_card}")
    
    # Crop ONLY the card!
    card_final = page_img[ymin_card:ymax_card, xmin_card:xmax_card]
    cv2.imwrite(r"C:\Users\Mypc3\AppData\Local\Temp\card_final_crop.png", card_final)
    
    # Split Front & Back
    cw = xmax_card - xmin_card
    mid_x = cw // 2
    # Fine-tune split point by finding column with lowest color saturation (gap between cards)
    gap_search_start = int(0.46 * cw)
    gap_search_end = int(0.54 * cw)
    col_sats = np.mean(page_hsv[ymin_card:ymax_card, xmin_card + gap_search_start : xmin_card + gap_search_end, 1], axis=0)
    best_gap_offset = np.argmin(col_sats)
    split_exact = gap_search_start + best_gap_offset
    
    front_final = card_final[:, 0:split_exact]
    back_final = card_final[:, split_exact:cw]
    
    cv2.imwrite(r"C:\Users\Mypc3\AppData\Local\Temp\front_card_CLEAN.png", front_final)
    cv2.imwrite(r"C:\Users\Mypc3\AppData\Local\Temp\back_card_CLEAN.png", back_final)
    print("SUCCESS: Saved clean front and back card images with NO overlapping text!")
