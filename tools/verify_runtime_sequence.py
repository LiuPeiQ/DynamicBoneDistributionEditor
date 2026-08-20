#!/usr/bin/env python3
"""Replay exported DynamicBone frames and validate RuntimeSequence v3.

The tool consumes DBDE.RuntimeSequence.v1/v2/v3 files.  It intentionally follows
the UpdateDynamicBones/UpdateParticles1/UpdateParticles2 order recovered from
Koikatu's DynamicBone.cs, then compares each predicted particle buffer with
the next post-LateUpdate game frame.

Collider transforms in v1 are reconstructed from world TRS.  A future export
adds exact endpoint data to remove the last approximation caused by Unity
hierarchical non-uniform scale.

RuntimeSequence v3 also records DynamicBone_Ver02 after LateUpdate.  Those
records are validated for completeness here; Blender replays their captured
Transform delta rather than feeding them into the standard DynamicBone solver.
"""

from __future__ import annotations

import argparse
import json
import math
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, Iterable, List, Sequence, Tuple


EPSILON = 1.0e-8
Vec3 = Tuple[float, float, float]
Quat = Tuple[float, float, float, float]


def vec3(value: Sequence[float]) -> Vec3:
    return (float(value[0]), float(value[1]), float(value[2]))


def add(a: Vec3, b: Vec3) -> Vec3:
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def subtract(a: Vec3, b: Vec3) -> Vec3:
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def multiply(a: Vec3, value: float) -> Vec3:
    return (a[0] * value, a[1] * value, a[2] * value)


def multiply_components(a: Vec3, b: Vec3) -> Vec3:
    return (a[0] * b[0], a[1] * b[1], a[2] * b[2])


def dot(a: Vec3, b: Vec3) -> float:
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def length(a: Vec3) -> float:
    return math.sqrt(dot(a, a))


def normalize(a: Vec3) -> Vec3:
    size = length(a)
    return multiply(a, 1.0 / size) if size > EPSILON else (0.0, 0.0, 0.0)


def quaternion_rotate(rotation: Quat, value: Vec3) -> Vec3:
    x, y, z, w = (float(component) for component in rotation)
    qx = w * value[0] + y * value[2] - z * value[1]
    qy = w * value[1] + z * value[0] - x * value[2]
    qz = w * value[2] + x * value[1] - y * value[0]
    qw = -x * value[0] - y * value[1] - z * value[2]
    return (
        qx * w + qw * -x + qy * -z - qz * -y,
        qy * w + qw * -y + qz * -x - qx * -z,
        qz * w + qw * -z + qx * -y - qy * -x,
    )


def transform_point(transform: dict, local: Vec3) -> Vec3:
    scale = vec3(transform.get("lossyScale", (1.0, 1.0, 1.0)))
    rotation = tuple(float(item) for item in transform.get("rotation", (0.0, 0.0, 0.0, 1.0)))
    return add(vec3(transform.get("position", (0.0, 0.0, 0.0))), quaternion_rotate(rotation, multiply_components(local, scale)))


@dataclass(frozen=True)
class Collider:
    point0: Vec3
    point1: Vec3
    radius: float
    inside: bool


def colliders_for_frame(frame: dict) -> List[Collider]:
    result: List[Collider] = []
    for source in frame.get("colliders", []):
        if not isinstance(source, dict) or not source.get("enabled", False):
            continue
        transform = source.get("transform")
        if not isinstance(transform, dict):
            continue
        if "worldEndpoint0" in source and "worldEndpoint1" in source:
            result.append(Collider(
                point0=vec3(source["worldEndpoint0"]),
                point1=vec3(source["worldEndpoint1"]),
                radius=float(source.get("worldRadius", source.get("radius", 0.0))),
                inside=int(source.get("bound", 0)) != 0,
            ))
            continue

        radius = float(source.get("radius", 0.0))
        scale = vec3(transform.get("lossyScale", (1.0, 1.0, 1.0)))
        radius *= abs(scale[2])
        center = vec3(source.get("center", (0.0, 0.0, 0.0)))
        half_segment = (float(source.get("height", 0.0)) - float(source.get("radius", 0.0))) * 0.5
        direction = int(source.get("direction", 0))
        point0 = center
        point1 = center
        if half_segment > 0.0:
            axis = [(1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0)][max(0, min(direction, 2))]
            point0 = subtract(center, multiply(axis, half_segment))
            point1 = add(center, multiply(axis, half_segment))
        result.append(Collider(
            point0=transform_point(transform, point0),
            point1=transform_point(transform, point1),
            radius=radius,
            inside=int(source.get("bound", 0)) != 0,
        ))
    return result


def collide(position: Vec3, particle_radius: float, collider: Collider) -> Vec3:
    radius = collider.radius + particle_radius
    radius_sq = radius * radius
    axis = subtract(collider.point1, collider.point0)
    to_position = subtract(position, collider.point0)
    projection = dot(to_position, axis)
    axis_sq = dot(axis, axis)
    if projection <= 0.0:
        closest = collider.point0
    elif projection >= axis_sq:
        closest = collider.point1
    elif axis_sq > EPSILON:
        closest = add(collider.point0, multiply(axis, projection / axis_sq))
    else:
        closest = collider.point0

    offset = subtract(position, closest)
    offset_sq = dot(offset, offset)
    should_project = offset_sq > radius_sq if collider.inside else (offset_sq > 0.0 and offset_sq < radius_sq)
    if not should_project:
        return position
    offset_length = math.sqrt(offset_sq)
    if offset_length <= EPSILON:
        return position
    return add(closest, multiply(offset, radius / offset_length))


def rest_target(particle: dict, parent_position: Vec3, parent_input_position: Vec3) -> Vec3:
    """Equivalent to parent.localToWorldMatrix with only translation replaced."""
    return add(vec3(particle["animatorWorldPosition"]), subtract(parent_position, parent_input_position))


def particle_name(particle: dict) -> str:
    return str(particle.get("transformPath", "")).replace("\\", "/").rsplit("/", 1)[-1]


def tick_count(elapsed: float, accumulator: float, update_rate: float) -> Tuple[int, float]:
    tick = 1.0 / max(update_rate, EPSILON)
    accumulator = max(accumulator, 0.0) + max(elapsed, 0.0)
    count = 0
    while accumulator + 1.0e-12 >= tick:
        accumulator -= tick
        count += 1
        if count >= 3:
            return count, 0.0
    return count, accumulator


def simulate_frame(previous_frame: dict, frame: dict, no_colliders: bool) -> Tuple[List[Vec3], List[Vec3], int, float]:
    previous_bone = previous_frame["dynamicBones"][0]
    dynamic_bone = frame["dynamicBones"][0]
    before = previous_bone["particles"]
    particles = dynamic_bone["particles"]
    if len(before) != len(particles):
        raise ValueError("Particle count changes during capture")

    positions = [vec3(item["position"]) for item in before]
    previous_positions = [vec3(item["previousPosition"]) for item in before]
    update_rate = float(dynamic_bone.get("updateRate", 60.0))
    count, accumulator = tick_count(
        float(frame.get("deltaTime", 0.0)),
        float(previous_bone.get("internalTimeAccumulator", 0.0)),
        update_rate,
    )
    object_scale = float(dynamic_bone.get("objectScale", 1.0))
    gravity = add(vec3(dynamic_bone.get("gravity", (0.0, 0.0, 0.0))), vec3(dynamic_bone.get("force", (0.0, 0.0, 0.0))))
    gravity = multiply(gravity, object_scale)
    object_move = vec3(dynamic_bone.get("objectMove", (0.0, 0.0, 0.0)))
    component_weight = float(dynamic_bone.get("weight", 1.0))
    collider_list = [] if no_colliders else colliders_for_frame(frame)

    def apply_constraints() -> None:
        for index in range(1, len(particles)):
            particle = particles[index]
            parent_index = int(particle["parentIndex"])
            parent_position = positions[parent_index]
            parent_input = vec3(particles[parent_index]["animatorWorldPosition"])
            target = rest_target(particle, parent_position, parent_input)
            elasticity = float(particle.get("elasticity", 0.0))
            stiffness = 1.0 + (float(particle.get("stiffness", 0.0)) - 1.0) * component_weight
            candidate = add(positions[index], multiply(subtract(target, positions[index]), elasticity))
            length_input = length(subtract(parent_input, vec3(particle["animatorWorldPosition"])))
            if stiffness > 0.0:
                offset = subtract(target, candidate)
                offset_length = length(offset)
                maximum = length_input * (1.0 - stiffness) * 2.0
                if offset_length > maximum and offset_length > EPSILON:
                    candidate = add(candidate, multiply(offset, (offset_length - maximum) / offset_length))
            for collider in collider_list:
                candidate = collide(candidate, float(particle.get("radius", 0.0)) * object_scale, collider)
            segment = subtract(parent_position, candidate)
            segment_length = length(segment)
            if segment_length > EPSILON:
                candidate = add(candidate, multiply(segment, (segment_length - length_input) / segment_length))
            positions[index] = candidate

    if count == 0:
        for index, particle in enumerate(particles):
            if int(particle["parentIndex"]) < 0:
                previous_positions[index] = positions[index]
                positions[index] = vec3(particle["animatorWorldPosition"])
            else:
                positions[index] = add(positions[index], object_move)
                previous_positions[index] = add(previous_positions[index], object_move)
        apply_constraints()
        return positions, previous_positions, count, accumulator

    for tick_index in range(count):
        move = object_move if tick_index == 0 else (0.0, 0.0, 0.0)
        for index, particle in enumerate(particles):
            if int(particle["parentIndex"]) < 0:
                previous_positions[index] = positions[index]
                positions[index] = vec3(particle["animatorWorldPosition"])
                continue
            inertia = float(particle.get("inertia", 0.0))
            follow_move = multiply(move, inertia)
            velocity = subtract(positions[index], previous_positions[index])
            previous_positions[index] = add(positions[index], follow_move)
            positions[index] = add(
                add(positions[index], multiply(velocity, 1.0 - float(particle.get("damping", 0.0)))),
                add(gravity, follow_move),
            )
        apply_constraints()
    return positions, previous_positions, count, accumulator


def rms(values: Iterable[float]) -> float:
    values = list(values)
    return math.sqrt(sum(value * value for value in values) / len(values)) if values else 0.0


def verify(sequence: dict, no_colliders: bool) -> List[dict]:
    frames = sequence.get("frames", [])
    if len(frames) < 2:
        raise ValueError("Sequence needs at least two completed frames")
    report = []
    for index in range(1, len(frames)):
        positions, previous, ticks, accumulator = simulate_frame(frames[index - 1], frames[index], no_colliders)
        actual = frames[index]["dynamicBones"][0]["particles"]
        position_errors = [length(subtract(positions[item], vec3(actual[item]["position"]))) for item in range(len(actual))]
        velocity_errors = [
            length(subtract(subtract(positions[item], previous[item]), vec3(actual[item]["velocity"])))
            for item in range(len(actual))
        ]
        report.append({
            "frame_index": int(frames[index].get("frameIndex", index)),
            "game_frame": int(frames[index].get("frameCount", 0)),
            "ticks": ticks,
            "accumulator": accumulator,
            "position_rms": rms(position_errors),
            "position_max": max(position_errors),
            "velocity_rms": rms(velocity_errors),
            "velocity_max": max(velocity_errors),
            "worst_particle": particle_name(actual[position_errors.index(max(position_errors))]),
        })
    return report


def validate_ver02_capture(sequence: dict) -> dict:
    """Validate the per-frame Ver02 payload added by RuntimeSequence v3."""
    if sequence.get("formatVersion") != "DBDE.RuntimeSequence.v3":
        return {"chain_count": 0, "record_count": 0, "roots": []}

    frames = sequence.get("frames", [])
    expected_count = int(sequence.get("dynamicBoneVer02Total", 0))
    if expected_count <= 0:
        raise ValueError("RuntimeSequence v3 has no DynamicBone_Ver02 chains")

    expected_roots = None
    particle_counts: Dict[str, int] = {}
    record_count = 0
    for frame_index, frame in enumerate(frames):
        game_frame = int(frame.get("frameCount", 0))
        records = frame.get("dynamicBonesVer02", [])
        if not isinstance(records, list) or len(records) != expected_count:
            raise ValueError(
                f"Frame {frame_index} expected {expected_count} Ver02 chains, "
                f"got {len(records) if isinstance(records, list) else 'invalid'}"
            )
        roots = [str(record.get("rootName", "")) for record in records]
        if expected_roots is None:
            expected_roots = roots
        elif roots != expected_roots:
            raise ValueError(
                f"Frame {frame_index} Ver02 root order changed: {roots}"
            )

        for record in records:
            root = str(record.get("rootName", ""))
            captured_frame = int(record.get("capturedPostLateUpdateFrame", -1))
            if captured_frame != game_frame:
                raise ValueError(
                    f"Frame {frame_index} {root} captured at {captured_frame}, "
                    f"expected {game_frame}"
                )
            particles = record.get("runtimeParticles", [])
            if not isinstance(particles, list) or not particles:
                raise ValueError(f"Frame {frame_index} {root} has no runtimeParticles")
            if root in particle_counts and len(particles) != particle_counts[root]:
                raise ValueError(
                    f"Frame {frame_index} {root} particle count changed from "
                    f"{particle_counts[root]} to {len(particles)}"
                )
            particle_counts[root] = len(particles)
            record_count += 1

    return {
        "chain_count": expected_count,
        "record_count": record_count,
        "roots": expected_roots or [],
        "particle_counts": particle_counts,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("sequence", type=Path, help="DBDE_LowerBodySkirtSequence_*.json")
    parser.add_argument("--no-colliders", action="store_true", help="isolate core Verlet/rest-length math")
    parser.add_argument("--json", action="store_true", help="print the report as JSON")
    args = parser.parse_args()

    with args.sequence.open("r", encoding="utf-8") as handle:
        document = json.load(handle)
    if document.get("formatVersion") not in {
        "DBDE.RuntimeSequence.v1",
        "DBDE.RuntimeSequence.v2",
        "DBDE.RuntimeSequence.v3",
    }:
        raise SystemExit("Expected DBDE.RuntimeSequence.v1, v2, or v3")

    ver02 = validate_ver02_capture(document)
    report = verify(document, args.no_colliders)
    if args.json:
        print(json.dumps(report, indent=2))
        return 0

    print("frame game_frame ticks pos_rms pos_max vel_rms vel_max worst")
    for row in report:
        print(
            "{frame_index:5d} {game_frame:10d} {ticks:5d} {position_rms:.9f} "
            "{position_max:.9f} {velocity_rms:.9f} {velocity_max:.9f} {worst_particle}".format(**row)
        )
    if report:
        print("summary position_rms={:.9f} position_max={:.9f}".format(
            rms(row["position_rms"] for row in report),
            max(row["position_max"] for row in report),
        ))
    if ver02["chain_count"]:
        print(
            "ver02 chains={} records={} roots={}".format(
                ver02["chain_count"],
                ver02["record_count"],
                ",".join(ver02["roots"]),
            )
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
