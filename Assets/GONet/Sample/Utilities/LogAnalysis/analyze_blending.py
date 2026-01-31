#!/usr/bin/env python3
"""
Analyzes GONet SoA blending diagnostics to detect interpolation vs extrapolation behavior.

Usage:
    python analyze_blending.py <log_file_path>
    python analyze_blending.py "C:/Users/.../logs/gonet-BlendDiag-2025-11-28.log"

Key Metrics:
    - dtTarget: Time delta between target (current time) and newest sample
        - dtTarget > 0: EXTRAPOLATING (predicting future, less smooth)
        - dtTarget <= 0: INTERPOLATING (blending between known data, smooth)
    - Extrapolation ratio: % of samples that are extrapolating
    - dtTarget distribution: How far ahead we're trying to predict

Expected healthy behavior:
    - With proper buffer lead time (250ms): ~0% extrapolation, dtTarget around -0.25s
    - Without buffer lead time: ~100% extrapolation, dtTarget around +0.02s to +0.1s
"""

import sys
import re
from dataclasses import dataclass, field
from typing import List, Dict, Optional, Tuple
from collections import defaultdict
import statistics


@dataclass
class BlendEntry:
    """Single blending measurement from logs"""
    role: str         # SVR or CLI
    frame: int
    elapsed_sec: float
    stream_type: str  # POS or ROT
    obj_idx: str      # e.g., "0:5"
    # NEW FORMAT fields (interpolation quality metrics)
    t_value: float    # Actual Lerp parameter (0-1). Ideal is 0.3-0.7 (middle of bracket)
    dt_bracket: float # Time gap between bracketing samples (should match sync rate ~24Hz=42ms or ~50Hz=20ms)
    dt_from_upper: float # Target relative to upper bracket sample (negative=interpolating, positive=extrapolating)
    bracket_idx: int  # Which sample pair is being used (0=oldest, higher=newer)
    valid_count: int  # Number of valid samples in ring buffer
    sample_age: float # How old the newest sample is (elapsedTicks - newestTicks) - should be ~0 if fresh
    is_extrap: bool   # Precomputed: dtFromUpper > 0
    is_physics: bool  # True if IsRigidBodyOwnerOnlyControlled
    gonet_id: int     # GONetId for this object
    is_mine: bool     # True if this machine is authority (IsMine) - blending should NOT happen for these!
    # LEGACY fields for backwards compatibility (computed from new fields or old format)
    dt_target: float = 0.0  # Legacy: dtFromUpper (for old analysis code)
    dt_samples: float = 0.0 # Legacy: dtBracket (for old analysis code)
    hist_count: int = 0     # Legacy: validCount
    write_idx: int = -1     # Legacy: not in new format
    strategy: int = 0       # Legacy: not in new format
    buffer_lead_sec: float = 0.15  # Legacy: assumed 150ms


@dataclass
class SummaryEntry:
    """Aggregate summary from logs"""
    frame: int
    elapsed_sec: float
    total_pos: int
    total_rot: int
    extrap_count: int
    interp_count: int
    avg_dt_target: float
    min_dt_target: float
    max_dt_target: float


@dataclass
class AnalysisResult:
    """Complete analysis results"""
    total_entries: int
    pos_entries: int
    rot_entries: int

    # Extrapolation metrics (the key diagnostic)
    extrap_count: int
    interp_count: int
    extrap_ratio: float  # 0.0 = all interpolating (good), 1.0 = all extrapolating (bad)

    # dtTarget distribution
    dt_target_values: List[float] = field(default_factory=list)
    dt_target_mean: float = 0.0
    dt_target_median: float = 0.0
    dt_target_stddev: float = 0.0
    dt_target_min: float = 0.0
    dt_target_max: float = 0.0
    dt_target_p95: float = 0.0  # 95th percentile

    # Buffer lead time
    buffer_lead_sec: float = 0.0

    # Time series for visualization
    frame_extrap_ratio: Dict[int, float] = field(default_factory=dict)

    # Health assessment
    health: str = "UNKNOWN"
    issues: List[str] = field(default_factory=list)


def parse_blend_line(line: str) -> Optional[BlendEntry]:
    """
    Parse a BLEND log line.
    NEW FORMAT: BLEND|role|frame|elapsedSec|streamType|objIdx|tValue|dtBracket|dtFromUpper|bracketIdx|validCount|sampleAge|isExtrap|isPhysics|gonetId|isMine
    OLD FORMAT: BLEND|role|frame|elapsedSec|streamType|objIdx|dtTarget|dtSamples|sampleAge|histCount|writeIdx|strategy|isExtrap|bufferLeadSec|isPhysics|gonetId|isMine
    """
    if not line.strip().startswith("BLEND|") and "BLEND|" not in line:
        return None

    # NEW FORMAT with tValue, dtBracket, dtFromUpper, bracketIdx, validCount:
    # BLEND|role|frame|elapsedSec|streamType|objIdx|tValue|dtBracket|dtFromUpper|bracketIdx|validCount|sampleAge|isExtrap|isPhysics|gonetId|isMine
    match = re.search(r'BLEND\|(SVR|CLI)\|(\d+)\|([\d.]+)\|(\w+)\|([\d:]+)\|([-\d.]+)\|([\d.]+)\|([-\d.]+)\|(\d+)\|(\d+)\|([-\d.]+)\|(\d+)\|(\d+)\|(\d+)\|(\d+)', line)
    if match:
        t_value = float(match.group(6))
        dt_bracket = float(match.group(7))
        dt_from_upper = float(match.group(8))
        bracket_idx = int(match.group(9))
        valid_count = int(match.group(10))
        sample_age = float(match.group(11))
        is_extrap = int(match.group(12)) == 1
        is_physics = int(match.group(13)) == 1
        gonet_id = int(match.group(14))
        is_mine = int(match.group(15)) == 1

        return BlendEntry(
            role=match.group(1),
            frame=int(match.group(2)),
            elapsed_sec=float(match.group(3)),
            stream_type=match.group(4),
            obj_idx=match.group(5),
            t_value=t_value,
            dt_bracket=dt_bracket,
            dt_from_upper=dt_from_upper,
            bracket_idx=bracket_idx,
            valid_count=valid_count,
            sample_age=sample_age,
            is_extrap=is_extrap,
            is_physics=is_physics,
            gonet_id=gonet_id,
            is_mine=is_mine,
            # Legacy fields computed from new format
            dt_target=dt_from_upper,
            dt_samples=dt_bracket,
            hist_count=valid_count
        )

    # OLD FORMAT with sampleAge: BLEND|role|frame|elapsedSec|streamType|objIdx|dtTarget|dtSamples|sampleAge|histCount|writeIdx|strategy|isExtrap|bufferLeadSec|isPhysics|gonetId|isMine
    match = re.search(r'BLEND\|(SVR|CLI)\|(\d+)\|([\d.]+)\|(\w+)\|([\d:]+)\|([-\d.]+)\|([\d.]+)\|([-\d.]+)\|(\d+)\|(\d+)\|(\d+)\|(\d+)\|([\d.]+)\|(\d+)\|(\d+)\|(\d+)', line)
    if match:
        dt_target = float(match.group(6))
        dt_samples = float(match.group(7))
        return BlendEntry(
            role=match.group(1),
            frame=int(match.group(2)),
            elapsed_sec=float(match.group(3)),
            stream_type=match.group(4),
            obj_idx=match.group(5),
            t_value=0.5,  # Unknown - old format
            dt_bracket=dt_samples,
            dt_from_upper=dt_target,
            bracket_idx=-1,  # Unknown - old format
            valid_count=int(match.group(9)),
            sample_age=float(match.group(8)),
            is_extrap=int(match.group(12)) == 1,
            is_physics=int(match.group(14)) == 1,
            gonet_id=int(match.group(15)),
            is_mine=int(match.group(16)) == 1,
            dt_target=dt_target,
            dt_samples=dt_samples,
            hist_count=int(match.group(9)),
            write_idx=int(match.group(10)),
            strategy=int(match.group(11)),
            buffer_lead_sec=float(match.group(13))
        )

    # OLD FORMAT with writeIdx but no sampleAge: BLEND|role|frame|elapsedSec|streamType|objIdx|dtTarget|dtSamples|histCount|writeIdx|strategy|isExtrap|bufferLeadSec|isPhysics|gonetId|isMine
    match = re.search(r'BLEND\|(SVR|CLI)\|(\d+)\|([\d.]+)\|(\w+)\|([\d:]+)\|([-\d.]+)\|([\d.]+)\|(\d+)\|(\d+)\|(\d+)\|(\d+)\|([\d.]+)\|(\d+)\|(\d+)\|(\d+)', line)
    if match:
        dt_target = float(match.group(6))
        dt_samples = float(match.group(7))
        return BlendEntry(
            role=match.group(1),
            frame=int(match.group(2)),
            elapsed_sec=float(match.group(3)),
            stream_type=match.group(4),
            obj_idx=match.group(5),
            t_value=0.5,  # Unknown - old format
            dt_bracket=dt_samples,
            dt_from_upper=dt_target,
            bracket_idx=-1,
            valid_count=int(match.group(8)),
            sample_age=-999.0,  # Unknown - old format without sampleAge
            is_extrap=int(match.group(11)) == 1,
            is_physics=int(match.group(13)) == 1,
            gonet_id=int(match.group(14)),
            is_mine=int(match.group(15)) == 1,
            dt_target=dt_target,
            dt_samples=dt_samples,
            hist_count=int(match.group(8)),
            write_idx=int(match.group(9)),
            strategy=int(match.group(10)),
            buffer_lead_sec=float(match.group(12))
        )

    # OLD FORMAT with role and isMine but no writeIdx: BLEND|role|frame|elapsedSec|streamType|objIdx|dtTarget|dtSamples|histCount|strategy|isExtrap|bufferLeadSec|isPhysics|gonetId|isMine
    match = re.search(r'BLEND\|(SVR|CLI)\|(\d+)\|([\d.]+)\|(\w+)\|([\d:]+)\|([-\d.]+)\|([\d.]+)\|(\d+)\|(\d+)\|(\d+)\|([\d.]+)\|(\d+)\|(\d+)\|(\d+)$', line)
    if match:
        dt_target = float(match.group(6))
        dt_samples = float(match.group(7))
        return BlendEntry(
            role=match.group(1),
            frame=int(match.group(2)),
            elapsed_sec=float(match.group(3)),
            stream_type=match.group(4),
            obj_idx=match.group(5),
            t_value=0.5,
            dt_bracket=dt_samples,
            dt_from_upper=dt_target,
            bracket_idx=-1,
            valid_count=int(match.group(8)),
            sample_age=-999.0,  # Unknown - old format
            is_extrap=int(match.group(10)) == 1,
            is_physics=int(match.group(12)) == 1,
            gonet_id=int(match.group(13)),
            is_mine=int(match.group(14)) == 1,
            dt_target=dt_target,
            dt_samples=dt_samples,
            hist_count=int(match.group(8)),
            strategy=int(match.group(9)),
            buffer_lead_sec=float(match.group(11))
        )

    # Format without isMine (backwards compatibility)
    match = re.search(r'BLEND\|(SVR|CLI)\|(\d+)\|([\d.]+)\|(\w+)\|([\d:]+)\|([-\d.]+)\|([\d.]+)\|(\d+)\|(\d+)\|(\d+)\|([\d.]+)\|(\d+)\|(\d+)$', line)
    if match:
        dt_target = float(match.group(6))
        dt_samples = float(match.group(7))
        return BlendEntry(
            role=match.group(1),
            frame=int(match.group(2)),
            elapsed_sec=float(match.group(3)),
            stream_type=match.group(4),
            obj_idx=match.group(5),
            t_value=0.5,
            dt_bracket=dt_samples,
            dt_from_upper=dt_target,
            bracket_idx=-1,
            valid_count=int(match.group(8)),
            sample_age=-999.0,  # Unknown - old format
            is_extrap=int(match.group(10)) == 1,
            is_physics=int(match.group(12)) == 1,
            gonet_id=int(match.group(13)),
            is_mine=False,  # Unknown
            dt_target=dt_target,
            dt_samples=dt_samples,
            hist_count=int(match.group(8)),
            strategy=int(match.group(9)),
            buffer_lead_sec=float(match.group(11))
        )

    # Old format without role (backwards compatibility)
    match = re.search(r'BLEND\|(\d+)\|([\d.]+)\|(\w+)\|([\d:]+)\|([-\d.]+)\|([\d.]+)\|(\d+)\|(\d+)\|(\d+)\|([\d.]+)(?:\|(\d+)\|(\d+))?', line)
    if match:
        is_physics = int(match.group(11)) == 1 if match.group(11) else False
        gonet_id = int(match.group(12)) if match.group(12) else 0
        dt_target = float(match.group(5))
        dt_samples = float(match.group(6))
        return BlendEntry(
            role="UNK",  # Unknown - old format
            frame=int(match.group(1)),
            elapsed_sec=float(match.group(2)),
            stream_type=match.group(3),
            obj_idx=match.group(4),
            t_value=0.5,
            dt_bracket=dt_samples,
            dt_from_upper=dt_target,
            bracket_idx=-1,
            valid_count=int(match.group(7)),
            sample_age=-999.0,  # Unknown - old format
            is_extrap=int(match.group(9)) == 1,
            is_physics=is_physics,
            gonet_id=gonet_id,
            is_mine=False,  # Unknown
            dt_target=dt_target,
            dt_samples=dt_samples,
            hist_count=int(match.group(7)),
            strategy=int(match.group(8)),
            buffer_lead_sec=float(match.group(10))
        )

    return None


def parse_summary_line(line: str) -> Optional[SummaryEntry]:
    """
    Parse a SUMMARY log line.
    Format: SUMMARY|frame|elapsedSec|totalPos|totalRot|extrapCount|interpCount|avgDtTarget|minDtTarget|maxDtTarget
    """
    if "SUMMARY|" not in line:
        return None

    match = re.search(r'SUMMARY\|(\d+)\|([\d.]+)\|(\d+)\|(\d+)\|(\d+)\|(\d+)\|([-\d.]+)\|([-\d.]+)\|([-\d.]+)', line)
    if not match:
        return None

    return SummaryEntry(
        frame=int(match.group(1)),
        elapsed_sec=float(match.group(2)),
        total_pos=int(match.group(3)),
        total_rot=int(match.group(4)),
        extrap_count=int(match.group(5)),
        interp_count=int(match.group(6)),
        avg_dt_target=float(match.group(7)),
        min_dt_target=float(match.group(8)),
        max_dt_target=float(match.group(9))
    )


def analyze_entries(entries: List[BlendEntry]) -> AnalysisResult:
    """Analyze parsed blend entries for issues.

    IMPORTANT: Health assessment is based on ACTIVE objects only (sampleAge < 0.5s).
    Stale/at-rest objects are reported separately but don't affect health score.
    """
    AT_REST_THRESHOLD = 0.5  # seconds

    if not entries:
        return AnalysisResult(total_entries=0, pos_entries=0, rot_entries=0,
                              extrap_count=0, interp_count=0, extrap_ratio=0.0,
                              health="NO_DATA", issues=["No blend entries found"])

    # Split into ACTIVE vs AT-REST based on sampleAge
    # ACTIVE = receiving fresh data, AT-REST = stale/inactive objects
    has_sample_age = any(e.sample_age > -999 for e in entries)
    if has_sample_age:
        active_entries = [e for e in entries if e.sample_age > -999 and e.sample_age < AT_REST_THRESHOLD]
        at_rest_entries = [e for e in entries if e.sample_age > -999 and e.sample_age >= AT_REST_THRESHOLD]
    else:
        # Old format without sampleAge - use all entries
        active_entries = entries
        at_rest_entries = []

    # Use ACTIVE entries for health metrics (this is what matters for visual quality)
    analysis_entries = active_entries if active_entries else entries

    pos_entries = [e for e in analysis_entries if e.stream_type == "POS"]
    rot_entries = [e for e in analysis_entries if e.stream_type == "ROT"]

    extrap_count = sum(1 for e in analysis_entries if e.is_extrap)
    interp_count = len(analysis_entries) - extrap_count
    extrap_ratio = extrap_count / len(analysis_entries) if analysis_entries else 0.0

    dt_values = [e.dt_target for e in analysis_entries]

    # Calculate statistics
    result = AnalysisResult(
        total_entries=len(entries),  # Report total for context
        pos_entries=len(pos_entries),
        rot_entries=len(rot_entries),
        extrap_count=extrap_count,
        interp_count=interp_count,
        extrap_ratio=extrap_ratio,
        dt_target_values=dt_values,
        dt_target_mean=statistics.mean(dt_values) if dt_values else 0,
        dt_target_median=statistics.median(dt_values) if dt_values else 0,
        dt_target_stddev=statistics.stdev(dt_values) if len(dt_values) > 1 else 0,
        dt_target_min=min(dt_values) if dt_values else 0,
        dt_target_max=max(dt_values) if dt_values else 0,
        buffer_lead_sec=entries[0].buffer_lead_sec if entries else 0.0
    )

    # 95th percentile
    sorted_dt = sorted(dt_values) if dt_values else []
    p95_idx = int(len(sorted_dt) * 0.95)
    result.dt_target_p95 = sorted_dt[p95_idx] if sorted_dt else 0.0

    # Calculate per-frame extrapolation ratio (active entries only)
    frame_entries = defaultdict(list)
    for e in analysis_entries:
        frame_entries[e.frame].append(e)

    for frame, frame_list in frame_entries.items():
        frame_extrap = sum(1 for e in frame_list if e.is_extrap)
        result.frame_extrap_ratio[frame] = frame_extrap / len(frame_list)

    # Health assessment (based on ACTIVE objects only)
    issues = []

    # Context: Report if many at-rest objects exist
    if at_rest_entries:
        at_rest_pct = len(at_rest_entries) / len(entries) * 100
        if at_rest_pct > 50:
            issues.append(f"INFO: {at_rest_pct:.0f}% of entries are AT-REST (stale objects, excluded from health metrics)")

    # Issue 1: High extrapolation ratio (ACTIVE objects only)
    if extrap_ratio > 0.9:
        issues.append(f"CRITICAL: {extrap_ratio*100:.1f}% extrapolation in ACTIVE objects")
    elif extrap_ratio > 0.3:
        issues.append(f"WARNING: {extrap_ratio*100:.1f}% extrapolation in ACTIVE objects (elevated)")

    # Issue 2: dtTarget consistently positive (ACTIVE objects only)
    if result.dt_target_mean > 0.05:  # > 50ms extrapolation on average
        issues.append(f"dtTarget mean is +{result.dt_target_mean*1000:.1f}ms (positive = extrapolating)")

    # Issue 3: Buffer lead time is 0
    if result.buffer_lead_sec < 0.01:
        issues.append(f"Buffer lead time is {result.buffer_lead_sec*1000:.1f}ms (should be ~150ms)")

    # Determine overall health
    if not issues or all("INFO:" in i for i in issues):
        result.health = "GOOD"
    elif any("CRITICAL" in i for i in issues):
        result.health = "CRITICAL"
    elif any("WARNING" in i for i in issues):
        result.health = "WARNING"
    else:
        result.health = "INFO"

    result.issues = issues
    return result


def parse_log_file(file_path: str) -> Tuple[List[BlendEntry], List[SummaryEntry], AnalysisResult]:
    """Parse log file and return entries + analysis"""

    blend_entries = []
    summary_entries = []

    try:
        with open(file_path, 'r', encoding='utf-8') as f:
            for line in f:
                blend = parse_blend_line(line)
                if blend:
                    blend_entries.append(blend)
                    continue

                summary = parse_summary_line(line)
                if summary:
                    summary_entries.append(summary)

    except FileNotFoundError:
        print(f"ERROR: Log file not found: {file_path}")
        sys.exit(1)
    except Exception as e:
        print(f"ERROR: Failed to read log file: {e}")
        sys.exit(1)

    result = analyze_entries(blend_entries)
    return blend_entries, summary_entries, result


def print_histogram(values: List[float], bins: int = 20, width: int = 50):
    """Print ASCII histogram of values"""
    if not values:
        print("  (no data)")
        return

    min_val = min(values)
    max_val = max(values)

    if min_val == max_val:
        print(f"  All values = {min_val:.4f}")
        return

    bin_width = (max_val - min_val) / bins
    bin_counts = [0] * bins

    for v in values:
        bin_idx = min(int((v - min_val) / bin_width), bins - 1)
        bin_counts[bin_idx] += 1

    max_count = max(bin_counts) if bin_counts else 1

    for i, count in enumerate(bin_counts):
        bin_start = min_val + i * bin_width
        bar_len = int((count / max_count) * width) if max_count > 0 else 0
        bar = "#" * bar_len
        marker = " <-- 0" if bin_start <= 0 < bin_start + bin_width else ""
        print(f"  {bin_start:+.3f}s |{bar} ({count}){marker}")


def print_report(result: AnalysisResult, entries: List[BlendEntry], summaries: List[SummaryEntry]):
    """Print analysis report"""

    print("=" * 80)
    print("GONet SoA Blending Analysis Report")
    print("=" * 80)
    print()

    # Health summary
    health_icons = {"GOOD": "[OK]", "WARNING": "[WARN]", "CRITICAL": "[FAIL]", "INFO": "[INFO]", "NO_DATA": "[?]"}
    print(f"Overall Health: {health_icons.get(result.health, '?')} {result.health}")
    if result.issues:
        for issue in result.issues:
            print(f"  - {issue}")
    print()

    # Entry statistics - show ACTIVE vs AT-REST breakdown
    AT_REST_THRESHOLD = 0.5
    has_sample_age = any(e.sample_age > -999 for e in entries)
    if has_sample_age:
        active_entries_report = [e for e in entries if e.sample_age > -999 and e.sample_age < AT_REST_THRESHOLD]
        at_rest_entries_report = [e for e in entries if e.sample_age > -999 and e.sample_age >= AT_REST_THRESHOLD]
    else:
        active_entries_report = entries
        at_rest_entries_report = []

    print("=" * 80)
    print("Entry Statistics")
    print("=" * 80)
    print(f"Total entries:     {result.total_entries}")
    if has_sample_age:
        print(f"  ACTIVE (sampleAge < 0.5s):  {len(active_entries_report)} <- used for health metrics")
        print(f"  AT-REST (stale/inactive):   {len(at_rest_entries_report)} <- excluded from health metrics")
    print(f"Position entries:  {result.pos_entries} (ACTIVE only)")
    print(f"Rotation entries:  {result.rot_entries} (ACTIVE only)")

    # Physics vs non-physics breakdown (ACTIVE only)
    physics_entries = [e for e in active_entries_report if e.is_physics]
    non_physics_entries = [e for e in active_entries_report if not e.is_physics]
    active_count = len(active_entries_report) if active_entries_report else 1
    print(f"Physics objects:   {len(physics_entries)} ({len(physics_entries)/active_count*100:.1f}%)" if active_entries_report else "Physics objects:   0")
    print(f"Non-physics:       {len(non_physics_entries)} ({len(non_physics_entries)/active_count*100:.1f}%)" if active_entries_report else "Non-physics:       0")
    print()

    # Extrapolation vs Interpolation (KEY METRIC - ACTIVE objects only)
    print("=" * 80)
    print("Extrapolation Analysis (ACTIVE objects only)")
    print("=" * 80)
    print(f"Extrapolating:   {result.extrap_count} ({result.extrap_ratio*100:.1f}%)")
    print(f"Interpolating:   {result.interp_count} ({(1-result.extrap_ratio)*100:.1f}%)")
    print()
    print("Explanation:")
    print("  - Extrapolating (dtTarget > 0): Predicting future, can cause jitter")
    print("  - Interpolating (dtTarget <= 0): Blending between known data, smooth")
    print("  - AT-REST objects (stale) are excluded - they don't affect visual quality")
    print()
    if result.extrap_ratio > 0.3:
        print("[PROBLEM] High extrapolation ratio in ACTIVE objects!")
    print()

    # dtTarget distribution (ACTIVE objects only)
    print("=" * 80)
    print("dtTarget Distribution - ACTIVE objects (seconds)")
    print("=" * 80)
    print(f"Mean:     {result.dt_target_mean:+.4f}s ({result.dt_target_mean*1000:+.1f}ms)")
    print(f"Median:   {result.dt_target_median:+.4f}s")
    print(f"Std Dev:  {result.dt_target_stddev:.4f}s")
    print(f"Min:      {result.dt_target_min:+.4f}s")
    print(f"Max:      {result.dt_target_max:+.4f}s")
    print(f"P95:      {result.dt_target_p95:+.4f}s")
    print()
    print("Histogram:")
    print_histogram(result.dt_target_values)
    print()

    # Buffer lead time
    print("=" * 80)
    print("Buffer Lead Time Configuration")
    print("=" * 80)
    print(f"Current setting: {result.buffer_lead_sec:.4f}s ({result.buffer_lead_sec*1000:.1f}ms)")
    if result.buffer_lead_sec < 0.01:
        print("[ISSUE] Buffer lead time is essentially 0!")
        print("        SoA blending should use: Time.ElapsedTicks - valueBlendingBufferLeadTicks")
        print("        Currently appears to use: Time.ElapsedTicks (no buffer)")
    else:
        expected_dt = -result.buffer_lead_sec
        print(f"Expected dtTarget with this buffer: ~{expected_dt:+.3f}s")
        print(f"Actual mean dtTarget:               ~{result.dt_target_mean:+.3f}s")
    print()

    # T-VALUE ANALYSIS (NEW KEY METRIC FOR INTERPOLATION QUALITY)
    # t_value is the actual Lerp parameter used for interpolation
    # Ideal: 0.3-0.7 (middle of bracket = smooth interpolation)
    # Edge cases: 0 or 1 (snapping to sample = jitter risk)
    # Use ACTIVE entries only for meaningful metrics
    has_t_value = any(e.t_value != 0.5 or e.bracket_idx >= 0 for e in active_entries_report)
    if has_t_value:
        print("=" * 80)
        print("T-VALUE ANALYSIS - ACTIVE objects (INTERPOLATION QUALITY)")
        print("=" * 80)
        print("t_value: The actual Lerp parameter (0-1) used for interpolation")
        print("  - Ideal range: 0.3-0.7 (smooth interpolation in middle of bracket)")
        print("  - Edge values (0 or 1): Snapping to sample boundary = potential jitter")
        print()

        t_values = [e.t_value for e in active_entries_report if e.bracket_idx >= 0]
        if t_values:
            t_mean = statistics.mean(t_values)
            t_median = statistics.median(t_values)
            t_stddev = statistics.stdev(t_values) if len(t_values) > 1 else 0

            # Count entries in different quality bands
            ideal_count = sum(1 for t in t_values if 0.3 <= t <= 0.7)
            edge_low_count = sum(1 for t in t_values if t < 0.1)
            edge_high_count = sum(1 for t in t_values if t > 0.9)
            boundary_count = edge_low_count + edge_high_count

            print(f"Statistics (ACTIVE entries only):")
            print(f"  Mean t:        {t_mean:.4f}")
            print(f"  Median t:      {t_median:.4f}")
            print(f"  Std Dev:       {t_stddev:.4f}")
            print()
            print(f"Quality Distribution:")
            print(f"  IDEAL (0.3-0.7):     {ideal_count}/{len(t_values)} ({ideal_count/len(t_values)*100:.1f}%)")
            print(f"  EDGE (<0.1 or >0.9): {boundary_count}/{len(t_values)} ({boundary_count/len(t_values)*100:.1f}%)")
            print(f"    - Near 0 (snap to older):  {edge_low_count}")
            print(f"    - Near 1 (snap to newer):  {edge_high_count}")
            print()

            # Quality assessment
            if ideal_count / len(t_values) > 0.7:
                print("[EXCELLENT] >70% of interpolations are in ideal range!")
            elif ideal_count / len(t_values) > 0.5:
                print("[GOOD] >50% of interpolations are in ideal range")
            elif boundary_count / len(t_values) > 0.3:
                print("[WARNING] >30% of interpolations are at edge values (snapping)")
                print("         This may cause visible micro-jitter")
            else:
                print("[OK] Interpolation quality is acceptable")

            # Histogram of t-values
            print()
            print("t-value Distribution:")
            print_histogram(t_values, bins=10, width=40)
        print()

        # dtBracket analysis (gap between bracketing samples) - ACTIVE only
        dt_brackets = [e.dt_bracket for e in active_entries_report if e.bracket_idx >= 0 and e.dt_bracket > 0]
        if dt_brackets:
            print("dtBracket Analysis (gap between bracketing samples):")
            dt_mean = statistics.mean(dt_brackets)
            print(f"  Mean:   {dt_mean*1000:.1f}ms")
            print(f"  Expected for 24Hz VALUE sync: ~42ms")
            print(f"  Expected for 50Hz physics sync: ~20ms")
            if dt_mean < 0.001:
                print("  [WARNING] dtBracket very small - may indicate anchor double-write issue")
            elif dt_mean > 0.1:
                print("  [WARNING] dtBracket > 100ms - samples arriving too slowly?")
            else:
                print("  [OK] dtBracket within expected range")
        print()

        # bracketIdx analysis (which sample pair is being used) - ACTIVE only
        bracket_indices = [e.bracket_idx for e in active_entries_report if e.bracket_idx >= 0]
        if bracket_indices:
            bracket_mean = statistics.mean(bracket_indices)
            bracket_max = max(bracket_indices)
            print(f"Bracket Index Analysis (which sample pair is used):")
            print(f"  Mean bracket index:  {bracket_mean:.2f}")
            print(f"  Max bracket index:   {bracket_max}")
            print(f"  (0 = oldest pair, higher = newer pairs)")
            if bracket_mean < 1:
                print("  [NOTE] Using mostly oldest pairs - buffer may be conservative")
            elif bracket_mean > 3:
                print("  [NOTE] Using mostly newer pairs - close to edge of buffer")
        print()

    # ACTIVE vs AT-REST Analysis (THE KEY INSIGHT)
    # Objects with fresh samples (sampleAge < 0.5s) are actively receiving data
    # Objects with stale samples (sampleAge >= 0.5s) are at rest or pending first update
    AT_REST_THRESHOLD = 0.5  # seconds

    has_sample_age = any(e.sample_age > -999 for e in entries)
    if has_sample_age:
        active_entries = [e for e in entries if e.sample_age > -999 and e.sample_age < AT_REST_THRESHOLD]
        at_rest_entries = [e for e in entries if e.sample_age > -999 and e.sample_age >= AT_REST_THRESHOLD]
        unknown_entries = [e for e in entries if e.sample_age <= -999]

        print("=" * 80)
        print("ACTIVE vs AT-REST Analysis (TRUE QUALITY METRIC)")
        print("=" * 80)
        print(f"Threshold: sampleAge < {AT_REST_THRESHOLD}s = ACTIVE, >= {AT_REST_THRESHOLD}s = AT-REST")
        print()
        print(f"ACTIVE (receiving data):   {len(active_entries)} entries")
        print(f"AT-REST (stale samples):   {len(at_rest_entries)} entries")
        if unknown_entries:
            print(f"UNKNOWN (old log format):  {len(unknown_entries)} entries")
        print()

        if active_entries:
            active_extrap = sum(1 for e in active_entries if e.is_extrap)
            active_dt_values = [e.dt_target for e in active_entries]
            active_mean = statistics.mean(active_dt_values)
            active_age_values = [e.sample_age for e in active_entries]
            active_age_mean = statistics.mean(active_age_values)
            print(f"ACTIVE OBJECTS (what matters for smoothness):")
            print(f"  Entries:        {len(active_entries)}")
            print(f"  Extrapolation:  {active_extrap}/{len(active_entries)} ({active_extrap/len(active_entries)*100:.1f}%)")
            print(f"  Mean dtTarget:  {active_mean:+.4f}s ({active_mean*1000:+.1f}ms)")
            print(f"  Mean sampleAge: {active_age_mean:.4f}s ({active_age_mean*1000:.1f}ms)")
            if active_extrap / len(active_entries) < 0.1:
                print(f"  [OK] Active objects are interpolating properly!")
            else:
                print(f"  [WARN] Active objects showing {active_extrap/len(active_entries)*100:.1f}% extrapolation")
        print()

        if at_rest_entries:
            at_rest_age_values = [e.sample_age for e in at_rest_entries]
            at_rest_age_mean = statistics.mean(at_rest_age_values)
            print(f"AT-REST OBJECTS (expected to have stale samples):")
            print(f"  Entries:        {len(at_rest_entries)}")
            print(f"  Mean sampleAge: {at_rest_age_mean:.4f}s ({at_rest_age_mean*1000:.1f}ms)")
            print(f"  [INFO] These are objects not currently receiving updates (at rest or spawned)")
        print()

    # Physics vs Non-Physics Comparison (KEY FOR DIAGNOSING ISSUES)
    physics_entries = [e for e in entries if e.is_physics]
    non_physics_entries = [e for e in entries if not e.is_physics]

    if physics_entries or non_physics_entries:
        print("=" * 80)
        print("Physics vs Non-Physics Comparison (KEY DIAGNOSTIC)")
        print("=" * 80)

        if physics_entries:
            phys_extrap = sum(1 for e in physics_entries if e.is_extrap)
            phys_dt_values = [e.dt_target for e in physics_entries]
            phys_mean = statistics.mean(phys_dt_values)
            print(f"PHYSICS OBJECTS ({len(physics_entries)} entries):")
            print(f"  Extrapolation:  {phys_extrap}/{len(physics_entries)} ({phys_extrap/len(physics_entries)*100:.1f}%)")
            print(f"  Mean dtTarget:  {phys_mean:+.4f}s ({phys_mean*1000:+.1f}ms)")
        else:
            print("PHYSICS OBJECTS: (none)")

        if non_physics_entries:
            non_phys_extrap = sum(1 for e in non_physics_entries if e.is_extrap)
            non_phys_dt_values = [e.dt_target for e in non_physics_entries]
            non_phys_mean = statistics.mean(non_phys_dt_values)
            print(f"NON-PHYSICS OBJECTS ({len(non_physics_entries)} entries):")
            print(f"  Extrapolation:  {non_phys_extrap}/{len(non_physics_entries)} ({non_phys_extrap/len(non_physics_entries)*100:.1f}%)")
            print(f"  Mean dtTarget:  {non_phys_mean:+.4f}s ({non_phys_mean*1000:+.1f}ms)")
        else:
            print("NON-PHYSICS OBJECTS: (none)")

        if physics_entries and non_physics_entries:
            phys_mean = statistics.mean([e.dt_target for e in physics_entries])
            non_phys_mean = statistics.mean([e.dt_target for e in non_physics_entries])
            diff = abs(phys_mean - non_phys_mean)
            print()
            print(f"DIFFERENCE: {diff*1000:.1f}ms")
            if diff > 0.05:  # 50ms difference
                print("[NOTE] Significant timing difference between physics and non-physics objects!")
                print("       This could indicate different time sources or update rates.")
        print()

    # Sample entries
    if entries:
        print("=" * 80)
        print("Sample Entries (first 10)")
        print("=" * 80)
        # Check if we have new format data
        has_new_format = any(e.bracket_idx >= 0 for e in entries)
        if has_new_format:
            print(f"{'Role':<4} {'Mine':<5} {'Frame':<8} {'Type':<4} {'Phys':<5} {'tValue':<8} {'dtBracket':<12} {'dtFromUpr':<12} {'BrkIdx':<7} {'Valid':<6} {'Extrap':<6} {'GONetId':<8}")
            print("-" * 110)
            for e in entries[:10]:
                extrap_str = "YES" if e.is_extrap else "no"
                phys_str = "Y" if e.is_physics else "N"
                mine_str = "AUTH" if e.is_mine else "recv"
                brk_idx_str = str(e.bracket_idx) if e.bracket_idx >= 0 else "?"
                # New format: show t_value, dt_bracket, dt_from_upper, bracket_idx
                print(f"{e.role:<4} {mine_str:<5} {e.frame:<8} {e.stream_type:<4} {phys_str:<5} {e.t_value:<8.4f} {e.dt_bracket*1000:>8.2f}ms   {e.dt_from_upper*1000:>8.2f}ms   {brk_idx_str:<7} {e.valid_count:<6} {extrap_str:<6} {e.gonet_id:<8}")
        else:
            # Legacy format
            print(f"{'Role':<4} {'Mine':<5} {'Frame':<8} {'Type':<4} {'Phys':<5} {'ObjIdx':<8} {'dtTarget':<12} {'dtSamples':<12} {'sampleAge':<12} {'Hist':<5} {'WrIdx':<6} {'Extrap':<6} {'GONetId':<8}")
            print("-" * 130)
            for e in entries[:10]:
                extrap_str = "YES" if e.is_extrap else "no"
                phys_str = "Y" if e.is_physics else "N"
                mine_str = "AUTH" if e.is_mine else "recv"
                write_idx_str = str(e.write_idx) if e.write_idx >= 0 else "?"
                sample_age_str = f"{e.sample_age:+.4f}s" if e.sample_age > -999 else "?"
                print(f"{e.role:<4} {mine_str:<5} {e.frame:<8} {e.stream_type:<4} {phys_str:<5} {e.obj_idx:<8} {e.dt_target:+.4f}s    {e.dt_samples:.6f}s  {sample_age_str:<12} {e.hist_count:<5} {write_idx_str:<6} {extrap_str:<6} {e.gonet_id:<8}")
        print()

    # Recommendations
    print("=" * 80)
    print("Recommendations")
    print("=" * 80)
    if result.health == "GOOD":
        print("[OK] Blending appears healthy. Objects should be interpolating smoothly.")
    elif result.extrap_ratio > 0.5:
        print("[FIX REQUIRED]")
        print("1. In GONet.cs line ~3242, change:")
        print("   FROM: SoA_BlendingPipeline.ScheduleBlendingJobs(ref SoAData, Time.ElapsedTicks);")
        print("   TO:   SoA_BlendingPipeline.ScheduleBlendingJobs(ref SoAData, Time.ElapsedTicks - valueBlendingBufferLeadTicks);")
        print()
        print("2. This will shift the target time 250ms into the past, allowing interpolation")
        print("   between known samples instead of extrapolating into the future.")
    else:
        print("Review the issues listed above.")
    print("=" * 80)


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        print("\nUsage: python analyze_blending.py <log_file_path>")
        print("\nExample:")
        print('  python analyze_blending.py "C:/Users/shash/AppData/LocalLow/Galore Interactive/GONetSandbox/logs/gonet-BlendDiag-2025-11-28.log"')
        sys.exit(1)

    log_file = sys.argv[1]
    print(f"Analyzing log file: {log_file}")
    print("Parsing...")

    entries, summaries, result = parse_log_file(log_file)

    if not entries:
        print("\nWARNING: No BLEND entries found in log file!")
        print("Make sure:")
        print("  1. LOG_BLEND_DIAG is defined in project settings")
        print("  2. You're looking at the BlendDiag profile log (gonet-BlendDiag-*.log)")
        print("  3. The test session ran long enough to generate data")
        sys.exit(1)

    # Split by role and authority
    svr_entries = [e for e in entries if e.role == "SVR"]
    cli_entries = [e for e in entries if e.role == "CLI"]
    unk_entries = [e for e in entries if e.role == "UNK"]

    # THE KEY FILTER: Non-authority entries (is_mine=False) - these are the ones being blended!
    non_authority_entries = [e for e in entries if not e.is_mine]
    authority_entries = [e for e in entries if e.is_mine]

    print(f"Found {len(entries)} blend entries, {len(summaries)} summaries")
    print(f"  Server (SVR): {len(svr_entries)}")
    print(f"  Client (CLI): {len(cli_entries)}")
    if unk_entries:
        print(f"  Unknown (old format): {len(unk_entries)}")
    print()
    print(f"  Authority (IsMine=true):     {len(authority_entries)} - NOT blended, metrics meaningless")
    print(f"  Non-Authority (IsMine=false): {len(non_authority_entries)} - BLENDED, this is what matters!")
    print()

    # THE IMPORTANT ANALYSIS: Non-authority entries only
    if non_authority_entries:
        print("=" * 80)
        print("NON-AUTHORITY ANALYSIS (IsMine=false) - THIS IS WHAT MATTERS FOR SMOOTHNESS")
        print("=" * 80)
        non_auth_result = analyze_entries(non_authority_entries)
        print_report(non_auth_result, non_authority_entries, summaries)

        # Further breakdown: non-authority by physics vs non-physics
        non_auth_physics = [e for e in non_authority_entries if e.is_physics]
        non_auth_non_physics = [e for e in non_authority_entries if not e.is_physics]

        if non_auth_physics and non_auth_non_physics:
            print("\n" + "=" * 80)
            print("NON-AUTHORITY BREAKDOWN: Physics vs Non-Physics")
            print("=" * 80)

            phys_extrap = sum(1 for e in non_auth_physics if e.is_extrap)
            phys_mean_dt = statistics.mean([e.dt_target for e in non_auth_physics])
            print(f"PHYSICS (non-authority): {len(non_auth_physics)} entries")
            print(f"  Extrapolation: {phys_extrap}/{len(non_auth_physics)} ({phys_extrap/len(non_auth_physics)*100:.1f}%)")
            print(f"  Mean dtTarget: {phys_mean_dt*1000:+.1f}ms")

            non_phys_extrap = sum(1 for e in non_auth_non_physics if e.is_extrap)
            non_phys_mean_dt = statistics.mean([e.dt_target for e in non_auth_non_physics])
            print(f"NON-PHYSICS (non-authority): {len(non_auth_non_physics)} entries")
            print(f"  Extrapolation: {non_phys_extrap}/{len(non_auth_non_physics)} ({non_phys_extrap/len(non_auth_non_physics)*100:.1f}%)")
            print(f"  Mean dtTarget: {non_phys_mean_dt*1000:+.1f}ms")

            diff = abs(phys_mean_dt - non_phys_mean_dt)
            print(f"\nDIFFERENCE: {diff*1000:.1f}ms")
            if diff > 0.02:  # 20ms difference
                print("[NOTE] Timing difference between physics and non-physics objects")
            print("=" * 80)
    else:
        print("[WARNING] No NON-AUTHORITY entries found!")
        print("          All logged entries are for objects this machine owns (IsMine=true).")
        print("          Blending only matters for objects owned by OTHER machines.")
        print()

    # Brief summary of authority entries (for context)
    if authority_entries:
        print("\n" + "=" * 80)
        print("AUTHORITY ENTRIES (IsMine=true) - Reference only, blending not applied")
        print("=" * 80)
        print(f"Total authority entries: {len(authority_entries)}")
        auth_svr = [e for e in authority_entries if e.role == "SVR"]
        auth_cli = [e for e in authority_entries if e.role == "CLI"]
        print(f"  Server-owned: {len(auth_svr)}, Client-owned: {len(auth_cli)}")
        print("=" * 80)


@dataclass
class DataInEntry:
    """Single DATA_IN entry from logs (position/rotation received from network)"""
    role: str         # SVR or CLI
    frame: int
    elapsed_sec: float
    stream_type: str  # POS or ROT
    gonet_id: int
    x: float
    y: float
    z: float
    w: float = 0.0    # Only for ROT
    ticks_at_send: int = 0
    is_anchor: bool = False
    is_physics: bool = False


def parse_data_in_entry(line: str) -> Optional[DataInEntry]:
    """Parse a DATA_IN log line into a DataInEntry object"""
    # Format: DATA_IN|role|frame|elapsedSec|POS|gonetId|x|y|z|ticksAtSend|isAnchor|isPhysics
    # Format: DATA_IN|role|frame|elapsedSec|ROT|gonetId|x|y|z|w|ticksAtSend|isAnchor|isPhysics
    if 'DATA_IN|' not in line:
        return None

    parts = line.split('|')
    if len(parts) < 12:
        return None

    try:
        role = parts[1]
        frame = int(parts[2])
        elapsed_sec = float(parts[3])
        stream_type = parts[4]
        gonet_id = int(parts[5])

        if stream_type == "POS":
            return DataInEntry(
                role=role,
                frame=frame,
                elapsed_sec=elapsed_sec,
                stream_type=stream_type,
                gonet_id=gonet_id,
                x=float(parts[6]),
                y=float(parts[7]),
                z=float(parts[8]),
                ticks_at_send=int(parts[9]),
                is_anchor=parts[10] == "1",
                is_physics=parts[11].strip() == "1"
            )
        elif stream_type == "ROT":
            return DataInEntry(
                role=role,
                frame=frame,
                elapsed_sec=elapsed_sec,
                stream_type=stream_type,
                gonet_id=gonet_id,
                x=float(parts[6]),
                y=float(parts[7]),
                z=float(parts[8]),
                w=float(parts[9]),
                ticks_at_send=int(parts[10]),
                is_anchor=parts[11] == "1",
                is_physics=parts[12].strip() == "1" if len(parts) > 12 else False
            )
    except (ValueError, IndexError):
        return None

    return None


def analyze_data_in_smoothness(entries: List[DataInEntry]) -> None:
    """Analyze DATA_IN entries for smoothness (velocity consistency, jitter)"""
    if not entries:
        print("\nNo DATA_IN entries found.")
        return

    print("\n" + "=" * 80)
    print("DATA_IN ANALYSIS (Received Network Values)")
    print("=" * 80)

    # Group by GONetId
    by_gonet_id = defaultdict(list)
    for e in entries:
        by_gonet_id[e.gonet_id].append(e)

    print(f"Total DATA_IN entries: {len(entries)}")
    print(f"Unique GONetIds: {len(by_gonet_id)}")
    print()

    # Separate position and rotation
    pos_entries = [e for e in entries if e.stream_type == "POS"]
    rot_entries = [e for e in entries if e.stream_type == "ROT"]
    print(f"Position entries: {len(pos_entries)}")
    print(f"Rotation entries: {len(rot_entries)}")
    print()

    # Analyze position smoothness per object
    print("-" * 80)
    print("PER-OBJECT POSITION ANALYSIS (velocity consistency)")
    print("-" * 80)
    print(f"{'GONetId':<10} {'Physics':<8} {'Entries':<8} {'Anchors':<8} {'AvgVel':<12} {'VelStdDev':<12} {'Jitter':<10}")
    print("-" * 80)

    for gonet_id in sorted(by_gonet_id.keys()):
        obj_entries = [e for e in by_gonet_id[gonet_id] if e.stream_type == "POS"]
        if len(obj_entries) < 3:
            continue

        # Sort by elapsed time
        obj_entries.sort(key=lambda x: x.elapsed_sec)

        # Calculate velocities between consecutive samples
        velocities = []
        time_deltas = []
        for i in range(1, len(obj_entries)):
            dt = obj_entries[i].elapsed_sec - obj_entries[i-1].elapsed_sec
            if dt > 0.001:  # At least 1ms apart
                dx = obj_entries[i].x - obj_entries[i-1].x
                dy = obj_entries[i].y - obj_entries[i-1].y
                dz = obj_entries[i].z - obj_entries[i-1].z
                dist = (dx*dx + dy*dy + dz*dz) ** 0.5
                vel = dist / dt
                velocities.append(vel)
                time_deltas.append(dt)

        if len(velocities) < 2:
            continue

        avg_vel = statistics.mean(velocities)
        vel_stddev = statistics.stdev(velocities) if len(velocities) > 1 else 0
        # Jitter = coefficient of variation (stddev / mean) - lower is smoother
        jitter = vel_stddev / avg_vel if avg_vel > 0.001 else 0

        is_physics = obj_entries[0].is_physics
        anchor_count = sum(1 for e in obj_entries if e.is_anchor)
        phys_str = "Y" if is_physics else "N"

        # Only show objects with significant data
        print(f"{gonet_id:<10} {phys_str:<8} {len(obj_entries):<8} {anchor_count:<8} {avg_vel:<12.2f} {vel_stddev:<12.2f} {jitter:<10.3f}")

    # Summary stats
    print()
    physics_entries = [e for e in entries if e.is_physics]
    non_physics_entries = [e for e in entries if not e.is_physics]
    print(f"Physics DATA_IN: {len(physics_entries)} ({len(physics_entries)/len(entries)*100:.1f}%)")
    print(f"Non-Physics DATA_IN: {len(non_physics_entries)} ({len(non_physics_entries)/len(entries)*100:.1f}%)")

    # Count anchors vs non-anchors
    anchor_entries = [e for e in entries if e.is_anchor]
    non_anchor_entries = [e for e in entries if not e.is_anchor]
    print(f"Anchor (VALUE) bundles: {len(anchor_entries)} ({len(anchor_entries)/len(entries)*100:.1f}%)")
    print(f"Non-Anchor (VELOCITY) bundles: {len(non_anchor_entries)} ({len(non_anchor_entries)/len(entries)*100:.1f}%)")
    print("=" * 80)


def parse_data_in_from_file(log_file: str) -> List[DataInEntry]:
    """Parse DATA_IN entries from log file"""
    entries = []
    try:
        with open(log_file, 'r', encoding='utf-8', errors='replace') as f:
            for line in f:
                entry = parse_data_in_entry(line)
                if entry:
                    entries.append(entry)
    except FileNotFoundError:
        print(f"Error: File not found: {log_file}")
    return entries


def analyze_object_timeline(blend_entries: List[BlendEntry], data_in_entries: List[DataInEntry], target_gonet_id: int = None):
    """
    Detailed timeline analysis for specific object(s).
    Shows state transitions, velocity patterns, and blend quality over time.
    """
    print("\n" + "=" * 80)
    print("PER-OBJECT TIMELINE ANALYSIS")
    print("=" * 80)

    # Group entries by GONetId
    blend_by_id = defaultdict(list)
    for e in blend_entries:
        if not e.is_mine:  # Only non-authority entries
            blend_by_id[e.gonet_id].append(e)

    data_in_by_id = defaultdict(list)
    for e in data_in_entries:
        data_in_by_id[e.gonet_id].append(e)

    # Find objects with high jitter or interesting patterns
    high_jitter_objects = []
    for gonet_id in sorted(blend_by_id.keys()):
        entries = [e for e in blend_by_id[gonet_id] if e.stream_type == "POS"]
        if len(entries) < 10:
            continue

        # Calculate jitter from sampleAge transitions
        has_active = any(e.sample_age > -999 and e.sample_age < 0.5 for e in entries)
        has_at_rest = any(e.sample_age > -999 and e.sample_age >= 0.5 for e in entries)

        # Get physics status
        is_physics = entries[0].is_physics if entries else False

        # Count state transitions (active <-> at_rest)
        transitions = 0
        prev_active = None
        for e in sorted(entries, key=lambda x: x.elapsed_sec):
            if e.sample_age > -999:
                is_active = e.sample_age < 0.5
                if prev_active is not None and is_active != prev_active:
                    transitions += 1
                prev_active = is_active

        # Calculate extrapolation during active periods
        active_entries = [e for e in entries if e.sample_age > -999 and e.sample_age < 0.5]
        if active_entries:
            active_extrap_pct = sum(1 for e in active_entries if e.is_extrap) / len(active_entries) * 100
        else:
            active_extrap_pct = 0

        high_jitter_objects.append({
            'gonet_id': gonet_id,
            'is_physics': is_physics,
            'total_entries': len(entries),
            'has_active': has_active,
            'has_at_rest': has_at_rest,
            'transitions': transitions,
            'active_extrap_pct': active_extrap_pct,
            'active_count': len(active_entries)
        })

    # Sort by transitions (most interesting first)
    high_jitter_objects.sort(key=lambda x: (-x['transitions'], -x['active_extrap_pct']))

    print(f"\nObjects with state transitions (active <-> at-rest):")
    print(f"{'GONetId':<10} {'Physics':<8} {'Entries':<8} {'Transitions':<12} {'Active%':<10} {'ActiveExtrap%':<15}")
    print("-" * 80)

    for obj in high_jitter_objects[:20]:  # Top 20
        phys_str = "Y" if obj['is_physics'] else "N"
        active_pct = obj['active_count'] / obj['total_entries'] * 100 if obj['total_entries'] > 0 else 0
        print(f"{obj['gonet_id']:<10} {phys_str:<8} {obj['total_entries']:<8} {obj['transitions']:<12} {active_pct:<10.1f} {obj['active_extrap_pct']:<15.1f}")

    # Detailed timeline for specific object or first high-transition object
    if target_gonet_id is None and high_jitter_objects:
        # Pick first physics object with transitions, or first with transitions
        target = next((o for o in high_jitter_objects if o['is_physics'] and o['transitions'] > 0), None)
        if target is None:
            target = next((o for o in high_jitter_objects if o['transitions'] > 0), None)
        if target:
            target_gonet_id = target['gonet_id']

    if target_gonet_id:
        print(f"\n{'='*80}")
        print(f"DETAILED TIMELINE: GONetId {target_gonet_id}")
        print(f"{'='*80}")

        # Get blend entries for this object
        obj_blend = sorted([e for e in blend_by_id[target_gonet_id] if e.stream_type == "POS"],
                          key=lambda x: x.elapsed_sec)
        obj_data_in = sorted([e for e in data_in_by_id[target_gonet_id] if e.stream_type == "POS"],
                             key=lambda x: x.elapsed_sec)

        if obj_blend:
            is_physics = obj_blend[0].is_physics
            print(f"Type: {'Physics' if is_physics else 'Non-Physics'}")
            print(f"Blend entries: {len(obj_blend)}, DATA_IN entries: {len(obj_data_in)}")

            # Show timeline segments
            print(f"\nTimeline (first 30 blend events):")
            print(f"{'Time':<10} {'Frame':<8} {'State':<10} {'dtTarget':<12} {'dtSamples':<12} {'sampleAge':<12} {'Extrap':<8}")
            print("-" * 80)

            for e in obj_blend[:30]:
                state = "ACTIVE" if e.sample_age > -999 and e.sample_age < 0.5 else "AT-REST" if e.sample_age > -999 else "?"
                extrap_str = "YES" if e.is_extrap else "no"
                age_str = f"{e.sample_age:.3f}s" if e.sample_age > -999 else "?"
                print(f"{e.elapsed_sec:<10.3f} {e.frame:<8} {state:<10} {e.dt_target:+.4f}s    {e.dt_samples:.6f}s  {age_str:<12} {extrap_str:<8}")

            # Show state transition moments
            print(f"\nState transitions:")
            prev_state = None
            for e in obj_blend:
                if e.sample_age > -999:
                    state = "ACTIVE" if e.sample_age < 0.5 else "AT-REST"
                    if state != prev_state and prev_state is not None:
                        print(f"  {e.elapsed_sec:.3f}s: {prev_state} -> {state} (dtTarget={e.dt_target:+.3f}s, sampleAge={e.sample_age:.3f}s)")
                    prev_state = state

            # DATA_IN velocity analysis
            if len(obj_data_in) >= 3:
                print(f"\nDATA_IN velocity pattern (sample):")
                print(f"{'Time':<10} {'Position':<30} {'Velocity':<15} {'Note':<20}")
                print("-" * 80)

                prev_pos = None
                prev_time = None
                for i, e in enumerate(obj_data_in[:20]):
                    pos_str = f"({e.x:.2f}, {e.y:.2f}, {e.z:.2f})"

                    if prev_pos is not None and prev_time is not None:
                        dt = e.elapsed_sec - prev_time
                        if dt > 0.001:
                            dist = ((e.x - prev_pos[0])**2 + (e.y - prev_pos[1])**2 + (e.z - prev_pos[2])**2) ** 0.5
                            vel = dist / dt
                            note = ""
                            if vel < 0.1:
                                note = "STATIONARY"
                            elif vel > 50:
                                note = "SPIKE!"
                            print(f"{e.elapsed_sec:<10.3f} {pos_str:<30} {vel:<15.2f} {note:<20}")
                        else:
                            print(f"{e.elapsed_sec:<10.3f} {pos_str:<30} {'<dt too small>':<15} ")
                    else:
                        print(f"{e.elapsed_sec:<10.3f} {pos_str:<30} {'(first)':<15}")

                    prev_pos = (e.x, e.y, e.z)
                    prev_time = e.elapsed_sec


def detect_blending_anomalies(blend_entries: List[BlendEntry]) -> List[dict]:
    """
    Detect anomalies in blending that could cause visual artifacts.
    Returns list of anomaly reports.
    """
    anomalies = []

    # Group by GONetId
    by_id = defaultdict(list)
    for e in blend_entries:
        if not e.is_mine:
            by_id[e.gonet_id].append(e)

    for gonet_id, entries in by_id.items():
        pos_entries = sorted([e for e in entries if e.stream_type == "POS"], key=lambda x: x.elapsed_sec)

        if len(pos_entries) < 5:
            continue

        is_physics = pos_entries[0].is_physics

        # Check for dtSamples = 0 (anchor double-write problem)
        zero_dt_samples = [e for e in pos_entries if e.dt_samples < 0.0001]
        if zero_dt_samples:
            anomalies.append({
                'gonet_id': gonet_id,
                'type': 'ZERO_DT_SAMPLES',
                'is_physics': is_physics,
                'count': len(zero_dt_samples),
                'message': f"GONetId {gonet_id}: {len(zero_dt_samples)} entries with dtSamples≈0 (anchor double-write issue)"
            })

        # Check for sudden dtTarget spikes during active periods
        active_entries = [e for e in pos_entries if e.sample_age > -999 and e.sample_age < 0.5]
        for i in range(1, len(active_entries)):
            dt_jump = abs(active_entries[i].dt_target - active_entries[i-1].dt_target)
            if dt_jump > 0.2:  # 200ms sudden change
                anomalies.append({
                    'gonet_id': gonet_id,
                    'type': 'DTTARGET_SPIKE',
                    'is_physics': is_physics,
                    'time': active_entries[i].elapsed_sec,
                    'message': f"GONetId {gonet_id}: dtTarget jumped {dt_jump*1000:.0f}ms at t={active_entries[i].elapsed_sec:.2f}s"
                })

        # Check for high extrapolation during active periods
        if active_entries:
            extrap_pct = sum(1 for e in active_entries if e.is_extrap) / len(active_entries)
            if extrap_pct > 0.2:  # >20% extrapolation during active
                anomalies.append({
                    'gonet_id': gonet_id,
                    'type': 'HIGH_ACTIVE_EXTRAP',
                    'is_physics': is_physics,
                    'count': len(active_entries),
                    'message': f"GONetId {gonet_id}: {extrap_pct*100:.1f}% extrapolation during active periods"
                })

    return anomalies


if __name__ == "__main__":
    main()

    # Parse optional target GONetId from command line
    target_gonet_id = None
    if len(sys.argv) >= 3:
        try:
            target_gonet_id = int(sys.argv[2])
            print(f"\n[Targeting specific GONetId: {target_gonet_id}]")
        except ValueError:
            pass

    # Also analyze DATA_IN if present
    if len(sys.argv) >= 2:
        log_file = sys.argv[1]
        data_in_entries = parse_data_in_from_file(log_file)
        if data_in_entries:
            analyze_data_in_smoothness(data_in_entries)

        # Timeline analysis
        blend_entries, _, _ = parse_log_file(log_file)
        non_auth_blend = [e for e in blend_entries if not e.is_mine]

        if non_auth_blend:
            analyze_object_timeline(non_auth_blend, data_in_entries, target_gonet_id)

            # Anomaly detection
            anomalies = detect_blending_anomalies(blend_entries)
            if anomalies:
                print("\n" + "=" * 80)
                print("ANOMALY DETECTION")
                print("=" * 80)

                # Group by type
                by_type = defaultdict(list)
                for a in anomalies:
                    by_type[a['type']].append(a)

                for atype, items in by_type.items():
                    print(f"\n{atype}: {len(items)} issues")
                    for item in items[:5]:  # Show first 5
                        print(f"  - {item['message']}")
                    if len(items) > 5:
                        print(f"  ... and {len(items)-5} more")
