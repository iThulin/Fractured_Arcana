#!/usr/bin/env python3
# Mirror of WorldAtlas3D.Frame.cs (provinces 7e, PR31): the whole-world fit inside the
# screen the panels leave free, with the pitch counted, and where the world then sits.
# Also checks the settlement marks' fade (WorldAtlas3D.Marks.cs) against the province
# names' (WorldAtlas3D.Provinces.cs). Prints the numbers Gate 7e is read against.
#
# Usage: python3 test_frame_7e.py

import math

COL_SPACING = 1.5
ROW_SPACING = math.sqrt(3.0)
ORTHO_SIZE_FACTOR = 1.5
CAM_DIST_MIN, CAM_DIST_MAX = 6.0, 150.0
OVERVIEW_PAD = 1.04

# StrategicView.Dispatch.UpdateAtlasInsets, in UI pixels.
COMMAND_WIDTH = 320
BAR_HEIGHT = 60
NEXT_STRIP_HEIGHT = 28
INSET_LEFT_PX = 16 + COMMAND_WIDTH + 16
INSET_TOP_PX = BAR_HEIGHT + 8 + NEXT_STRIP_HEIGHT
INSET_RIGHT_PX = 16
INSET_BOTTOM_PX = 44

MARK_FADE_FROM, MARK_GONE_AT = 0.30, 0.20
NAME_FADE_FROM, NAME_FULL_AT = 0.45, 0.34


def clamp(x, lo, hi):
    return max(lo, min(hi, x))


def pitch_deg(dist, override=-1.0):
    if override >= 0:
        return override
    return 40.0 + 28.0 * clamp((dist - CAM_DIST_MIN) / (CAM_DIST_MAX - CAM_DIST_MIN), 0.0, 1.0)


def fit_world_dist(width, height, aspect, left, top, right, bottom, override=-1.0):
    w = width * COL_SPACING
    h = height * ROW_SPACING
    free_w = max(0.1, 1.0 - left - right)
    free_h = max(0.1, 1.0 - top - bottom)
    dist = CAM_DIST_MAX
    for _ in range(6):
        sin_pitch = math.sin(math.radians(pitch_deg(dist, override)))
        view_h = max(h * sin_pitch / free_h, w / (max(0.1, aspect) * free_w)) * OVERVIEW_PAD
        dist = clamp(view_h / ORTHO_SIZE_FACTOR, CAM_DIST_MIN, CAM_DIST_MAX)
    return dist


def old_overview_dist(width, height, aspect):
    w = width * COL_SPACING
    h = height * ROW_SPACING
    return clamp(max(h, w / aspect) * OVERVIEW_PAD / ORTHO_SIZE_FACTOR, CAM_DIST_MIN, CAM_DIST_MAX)


def mark_alpha(zoom01):
    return clamp((zoom01 - MARK_GONE_AT) / (MARK_FADE_FROM - MARK_GONE_AT), 0.0, 1.0)


def name_alpha(zoom01):
    return clamp((NAME_FADE_FROM - zoom01) / (NAME_FADE_FROM - NAME_FULL_AT), 0.0, 1.0)


def report(width, height, ui_w, ui_h):
    left, top = INSET_LEFT_PX / ui_w, INSET_TOP_PX / ui_h
    right, bottom = INSET_RIGHT_PX / ui_w, INSET_BOTTOM_PX / ui_h
    aspect = ui_w / ui_h
    dist = fit_world_dist(width, height, aspect, left, top, right, bottom)
    view_h = dist * ORTHO_SIZE_FACTOR
    ppu = ui_h / view_h
    pitch = math.radians(pitch_deg(dist))
    world_w = width * COL_SPACING * ppu
    world_h = height * ROW_SPACING * math.sin(pitch) * ppu
    cx = ui_w * (0.5 + (left - right) / 2.0)
    cy = ui_h * (0.5 + (top - bottom) / 2.0)
    x0, x1 = cx - world_w / 2.0, cx + world_w / 2.0
    y0, y1 = cy - world_h / 2.0, cy + world_h / 2.0
    free = (INSET_LEFT_PX, INSET_TOP_PX, ui_w - INSET_RIGHT_PX, ui_h - INSET_BOTTOM_PX)
    inside = x0 >= free[0] - 0.5 and y0 >= free[1] - 0.5 and x1 <= free[2] + 0.5 and y1 <= free[3] + 0.5
    old = old_overview_dist(width, height, aspect)
    print(f"world {width}x{height} on a {ui_w}x{ui_h} UI: distance {dist:.2f} (was {old:.2f}), "
          f"pitch {math.degrees(pitch):.1f}, {ppu:.2f} px a unit")
    print(f"  the world draws {world_w:.0f} x {world_h:.0f} at x {x0:.0f} to {x1:.0f}, y {y0:.0f} to {y1:.0f}; "
          f"free screen x {free[0]} to {free[2]}, y {free[1]} to {free[3]}: {'inside' if inside else 'OUTSIDE'}")
    return inside


def main():
    ok = True
    for ui in ((1920, 1080), (1920, 1200), (2520, 1080), (1920, 1440)):
        ok &= report(158, 96, *ui)
    ok &= report(96, 64, 1920, 1080)
    zoom_overview = (fit_world_dist(158, 96, 16 / 9, INSET_LEFT_PX / 1920, INSET_TOP_PX / 1080,
                                    INSET_RIGHT_PX / 1920, INSET_BOTTOM_PX / 1080) - CAM_DIST_MIN) / (CAM_DIST_MAX - CAM_DIST_MIN)
    print(f"marks at the whole-world view (zoom01 {zoom_overview:.2f}): {mark_alpha(zoom_overview):.2f}; "
          f"names there: {name_alpha(zoom_overview):.2f}")
    for z in (0.15, 0.20, 0.25, 0.30, 0.34, 0.40, 0.45):
        print(f"  zoom01 {z:.2f}: marks {mark_alpha(z):.2f}, names {name_alpha(z):.2f}")
    assert mark_alpha(zoom_overview) == 1.0
    assert mark_alpha(MARK_GONE_AT) == 0.0
    print("PASS" if ok else "FAIL")


if __name__ == "__main__":
    main()
