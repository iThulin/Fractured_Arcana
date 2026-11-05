#!/usr/bin/env python3
# Mirror of WorldAtlas3D.Frame.cs as section 7f leaves it (PR39, PR40): the frame around the
# world, the whole-world fit of the framed world inside the screen the panels leave free (the
# column gone: left and right 16, top 96, bottom 44), with the pitch counted, and where the
# world and its frame then sit. Also the sea's glint fade (WorldAtlas3D.Clarity.cs, PR36) and
# the corner blocks' overlap with the framed world at the whole-world view.
#
# Usage: python3 test_frame_7f.py

import math

HEX_R = 1.0
COL_SPACING = 1.5 * HEX_R
ROW_SPACING = math.sqrt(3.0) * HEX_R
ORTHO_SIZE_FACTOR = 1.5
CAM_DIST_MIN, CAM_DIST_MAX = 6.0, 150.0
OVERVIEW_PAD = 1.04

FRAME_WIDTH = 3.0
FRAME_OVERLAP = 0.25

# StrategicView.Dispatch.UpdateAtlasInsets after 7f, in UI pixels.
BAR_HEIGHT = 60
NEXT_STRIP_HEIGHT = 28
INSET_LEFT_PX = 16
INSET_TOP_PX = BAR_HEIGHT + 8 + NEXT_STRIP_HEIGHT
INSET_RIGHT_PX = 16
INSET_BOTTOM_PX = 44

# The corner blocks (StrategicView.Corners), bottom anchored: width, and the height of the
# roster's chips plus the selection panel with two verbs (the castle) and the folded
# destinations header above them.
CORNER_WIDTH = 440
CORNER_HEIGHT = 34 + 8 + 176 + 8 + 44
LENS_WIDTH = 392
LENS_HEIGHT = 52

SEA_GLINT_FULL_AT, SEA_GLINT_GONE_AT, SEA_GLINT_STRENGTH = 0.10, 0.30, 0.35


def clamp(x, lo, hi):
    return max(lo, min(hi, x))


def pitch_deg(dist):
    return 40.0 + 28.0 * clamp((dist - CAM_DIST_MIN) / (CAM_DIST_MAX - CAM_DIST_MIN), 0.0, 1.0)


def frame_inner(width, height):
    x0 = -HEX_R * 0.5 + FRAME_OVERLAP
    x1 = (width - 1) * COL_SPACING + HEX_R * 0.5 - FRAME_OVERLAP
    z0 = FRAME_OVERLAP
    z1 = (height - 1) * ROW_SPACING + ROW_SPACING * 0.5 - FRAME_OVERLAP
    return x0, z0, x1, z1


def world_center(width, height):
    return width * COL_SPACING * 0.5, height * ROW_SPACING * 0.5


def fit_extent(width, height):
    x0, z0, x1, z1 = frame_inner(width, height)
    x0, z0, x1, z1 = x0 - FRAME_WIDTH, z0 - FRAME_WIDTH, x1 + FRAME_WIDTH, z1 + FRAME_WIDTH
    cx, cz = world_center(width, height)
    return 2 * max(cx - x0, x1 - cx), 2 * max(cz - z0, z1 - cz), (x0, z0, x1, z1)


def fit_world_dist(width, height, aspect, left, top, right, bottom):
    w, h, _ = fit_extent(width, height)
    free_w = max(0.1, 1.0 - left - right)
    free_h = max(0.1, 1.0 - top - bottom)
    dist = CAM_DIST_MAX
    for _ in range(6):
        sin_pitch = math.sin(math.radians(pitch_deg(dist)))
        view_h = max(h * sin_pitch / free_h, w / (max(0.1, aspect) * free_w)) * OVERVIEW_PAD
        dist = clamp(view_h / ORTHO_SIZE_FACTOR, CAM_DIST_MIN, CAM_DIST_MAX)
    return dist


def glint(zoom01):
    return SEA_GLINT_STRENGTH * clamp((SEA_GLINT_GONE_AT - zoom01) / (SEA_GLINT_GONE_AT - SEA_GLINT_FULL_AT), 0.0, 1.0)


def report(width, height, ui_w, ui_h):
    left, top = INSET_LEFT_PX / ui_w, INSET_TOP_PX / ui_h
    right, bottom = INSET_RIGHT_PX / ui_w, INSET_BOTTOM_PX / ui_h
    aspect = ui_w / ui_h
    dist = fit_world_dist(width, height, aspect, left, top, right, bottom)
    view_h = dist * ORTHO_SIZE_FACTOR
    ppu = ui_h / view_h
    pitch = math.radians(pitch_deg(dist))
    sin_p = math.sin(pitch)
    _, _, (ox0, oz0, ox1, oz1) = fit_extent(width, height)
    cx, cz = world_center(width, height)
    # The camera's target (WorldCenter) shows at the free screen's centre.
    scx = ui_w * (0.5 + (left - right) / 2.0)
    scy = ui_h * (0.5 + (top - bottom) / 2.0)
    sx0, sx1 = scx + (ox0 - cx) * ppu, scx + (ox1 - cx) * ppu
    sy0, sy1 = scy + (oz0 - cz) * sin_p * ppu, scy + (oz1 - cz) * sin_p * ppu
    free = (INSET_LEFT_PX, INSET_TOP_PX, ui_w - INSET_RIGHT_PX, ui_h - INSET_BOTTOM_PX)
    inside = sx0 >= free[0] - 0.5 and sy0 >= free[1] - 0.5 and sx1 <= free[2] + 0.5 and sy1 <= free[3] + 0.5
    zoom01 = (dist - CAM_DIST_MIN) / (CAM_DIST_MAX - CAM_DIST_MIN)
    print(f"world {width}x{height} on a {ui_w}x{ui_h} UI: distance {dist:.2f}, zoom01 {zoom01:.2f}, "
          f"pitch {math.degrees(pitch):.1f}, {ppu:.2f} px a unit, frame {FRAME_WIDTH * ppu:.0f} px wide")
    print(f"  the framed world draws {sx1 - sx0:.0f} x {sy1 - sy0:.0f} at x {sx0:.0f} to {sx1:.0f}, "
          f"y {sy0:.0f} to {sy1:.0f}; free screen x {free[0]} to {free[2]}, y {free[1]} to {free[3]}: "
          f"{'inside' if inside else 'OUTSIDE'}")
    # Corner blocks over the framed world (bottom-left stack, bottom-right lenses).
    bl = (16, ui_h - 16 - CORNER_HEIGHT, 16 + CORNER_WIDTH, ui_h - 16)
    br = (ui_w - 16 - LENS_WIDTH, ui_h - 16 - LENS_HEIGHT, ui_w - 16, ui_h - 16)
    for name, (ax0, ay0, ax1, ay1) in (("bottom-left", bl), ("bottom-right", br)):
        ow = max(0.0, min(ax1, sx1) - max(ax0, sx0))
        oh = max(0.0, min(ay1, sy1) - max(ay0, sy0))
        print(f"  {name} block over the framed world: {ow:.0f} x {oh:.0f} px")
    print(f"  the sea's glints at this zoom: {glint(zoom01):.3f} (0 means none)")
    return inside


def main():
    ok = True
    for ui in ((1920, 1080), (1920, 1200), (2560, 1080), (1920, 1440)):
        ok &= report(158, 96, *ui)
    ok &= report(96, 64, 1920, 1080)
    x0, z0, x1, z1 = frame_inner(158, 96)
    print(f"frame inner edge (158 x 96): x {x0:.2f} to {x1:.2f}, z {z0:.2f} to {z1:.2f}")
    for z in (0.0, 0.05, 0.10, 0.20, 0.30, 0.50, 0.80):
        print(f"  zoom01 {z:.2f}: glint strength {glint(z):.3f}")
    assert glint(0.80) == 0.0 and abs(glint(0.10) - SEA_GLINT_STRENGTH) < 1e-9
    print("PASS" if ok else "FAIL")


if __name__ == "__main__":
    main()
