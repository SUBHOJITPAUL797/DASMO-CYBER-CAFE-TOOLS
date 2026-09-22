import cv2
import numpy as np

def detect_page_sheet(img_bgr):
    """If the image is a screenshot or photo of an A4 page on a dark/colored background, detects the page boundary."""
    gray = cv2.cvtColor(img_bgr, cv2.COLOR_BGR2GRAY)
    h, w = img_bgr.shape[:2]

    # Threshold bright white page
    _, thresh = cv2.threshold(gray, 220, 255, cv2.THRESH_BINARY)
    contours, _ = cv2.findContours(thresh, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)

    for c in contours:
        x, y, cw, ch = cv2.boundingRect(c)
        # Page should take a significant part of the view
        if cw > 0.25 * w and ch > 0.4 * h:
            ar = cw / float(ch)
            # A4 aspect ratio is ~ 0.707 (0.5 to 0.9)
            if 0.5 <= ar <= 0.9:
                return (x, y, cw, ch)

    return (0, 0, w, h)

# Test on media_1788009337899.png
img = cv2.imread(r"C:\Users\Mypc3\.gemini\antigravity\brain\8d4b03a4-fd55-402d-af30-656b997e2221\.user_uploaded\media_1788009337899.png")
px, py, pw, ph = detect_page_sheet(img)
print(f"Detected page sheet: x={px}, y={py}, w={pw}, h={ph}")

page_crop = img[py:py+ph, px:px+pw]
cv2.imwrite(r"C:\Users\Mypc3\AppData\Local\Temp\detected_page.png", page_crop)

# Inside the page, find the card
page_gray = cv2.cvtColor(page_crop, cv2.COLOR_BGR2GRAY)
page_hsv = cv2.cvtColor(page_crop, cv2.COLOR_BGR2HSV)

# Color mask for WB ration card (blue/cyan) or bottom 30% of page
sat = page_hsv[:, :, 1]
val = page_hsv[:, :, 2]
card_color_mask = ((sat > 30) & (val > 80)).astype(np.uint8) * 255

# Look in bottom 40% of page
roi_y = int(0.60 * ph)
roi_mask = card_color_mask[roi_y:ph, :]

coords = np.argwhere(roi_mask > 0)
if len(coords) > 0:
    ymin, xmin = coords.min(axis=0)
    ymax, xmax = coords.max(axis=0) + 1
    card_y = roi_y + ymin
    card_h = ymax - ymin
    card_x = xmin
    card_w = xmax - xmin
    print(f"Inside Page: Card detected at x={card_x}, y={card_y}, w={card_w}, h={card_h}")

    card_img = page_crop[card_y:card_y+card_h, card_x:card_x+card_w]
    cv2.imwrite(r"C:\Users\Mypc3\AppData\Local\Temp\detected_card_perfect.png", card_img)

    # Split front and back
    mid = card_w // 2
    cv2.imwrite(r"C:\Users\Mypc3\AppData\Local\Temp\detected_front_perfect.png", card_img[:, 0:mid])
    cv2.imwrite(r"C:\Users\Mypc3\AppData\Local\Temp\detected_back_perfect.png", card_img[:, mid:card_w])
    print("SAVED PERFECT FRONT AND BACK CARDS!")
