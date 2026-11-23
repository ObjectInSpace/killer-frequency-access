"""Builds the mod's walking graph from a ripped NavMeshData asset (Unity's Detour tiles, version 16).

usage: python -I gen_navmesh.py <NavMesh.asset> <out.bin.gz>

The game strips NavMeshPath and NavMesh.CalculateTriangulation, so the mod cannot route over the
NavMesh at runtime. This writes the NavMesh's own polygons and their neighbour links instead.

Tile layout (checked against NavMesh_0 of SCN_Main): a 72-byte header (magic "VAND", version 16, tile x,
tile y, layer, polyCount, vertCount, ...), vertCount * 3 floats, then polyCount 32-byte polygons:
6 ushort vertex indices, 6 ushort neighbour codes (0 none, 0x8000 bit = tile border, else index + 1),
uint flags, byte vertex count, byte area.

Neighbours across tile borders are not stored in the asset (Detour links them at load); they are
matched here like Detour does: border edges on the same line that overlap, at a close height.

Output (gzip, little-endian): "KFNV", int version 1, int polyCount, then per polygon:
byte vertexCount, vertexCount * (x, y, z) floats, ushort linkCount, then per link:
int otherPolygon, portal end A (x, y, z), portal end B (x, y, z).
"""
import gzip
import re
import struct
import sys
from collections import defaultdict

BORDER_HEIGHT_TOLERANCE = 0.3


def read_polys(path):
    text = open(path).read()
    tiles = [bytes.fromhex(h) for h in re.findall(r'm_MeshData: ([0-9a-f]+)', text)]
    polys = []  # (tile, verts, neis)
    for ti, b in enumerate(tiles):
        magic, ver, _tx, _ty, _layer, npoly, nvert = struct.unpack_from('<4s6i', b, 0)
        if magic != b'VAND' or ver != 16:
            raise SystemExit(f'tile {ti}: unexpected header {magic} {ver}')
        off = 72
        verts = [struct.unpack_from('<3f', b, off + 12 * i) for i in range(nvert)]
        off += 12 * nvert
        for p in range(npoly):
            vi = struct.unpack_from('<6H', b, off)
            ne = struct.unpack_from('<6H', b, off + 12)
            _flags, vc, _area = struct.unpack_from('<IBB', b, off + 24)
            off += 32
            if not (3 <= vc <= 6 and all(v < nvert for v in vi[:vc])):
                raise SystemExit(f'tile {ti} poly {p}: bad polygon {vi} {vc}')
            polys.append((ti, [verts[v] for v in vi[:vc]], ne[:vc]))
    return polys


def lerp(a, b, t):
    return tuple(a[k] + (b[k] - a[k]) * t for k in range(3))


def build_links(polys):
    first = {}
    for i, (ti, _v, _n) in enumerate(polys):
        first.setdefault(ti, i)
    links = defaultdict(dict)  # poly -> other -> (a, b)
    border = defaultdict(list)
    for i, (ti, vs, ne) in enumerate(polys):
        for j, n in enumerate(ne):
            a, b = vs[j], vs[(j + 1) % len(vs)]
            if n == 0:
                continue
            if n & 0x8000:
                if abs(a[0] - b[0]) < 1e-3:
                    border[('x', round(a[0], 2))].append((i, 2, a, b))
                elif abs(a[2] - b[2]) < 1e-3:
                    border[('z', round(a[2], 2))].append((i, 0, a, b))
            else:
                links[i][first[ti] + n - 1] = (a, b)
    for edges in border.values():
        for k, (i, axis, a, b) in enumerate(edges):
            lo, hi = sorted((a[axis], b[axis]))
            for i2, _axis2, a2, b2 in edges[k + 1:]:
                if polys[i][0] == polys[i2][0]:
                    continue
                lo2, hi2 = sorted((a2[axis], b2[axis]))
                s, e = max(lo, lo2), min(hi, hi2)
                if e - s < 0.01:
                    continue
                # Portal on edge (a, b) between s and e.
                span = b[axis] - a[axis]
                pa = lerp(a, b, (s - a[axis]) / span)
                pb = lerp(a, b, (e - a[axis]) / span)
                span2 = b2[axis] - a2[axis]
                qa = lerp(a2, b2, (s - a2[axis]) / span2)
                qb = lerp(a2, b2, (e - a2[axis]) / span2)
                if abs(pa[1] - qa[1]) > BORDER_HEIGHT_TOLERANCE or abs(pb[1] - qb[1]) > BORDER_HEIGHT_TOLERANCE:
                    continue
                # Portal ends in the order of poly i's edge (a to b), like the internal links: the router
                # reads the first end as the left side. pa/pb are ordered by coordinate, so flip them when
                # the edge runs toward smaller values. Poly i2's edge runs the other way.
                if a[axis] > b[axis]:
                    pa, pb = pb, pa
                links[i][i2] = (pa, pb)
                links[i2][i] = (pb, pa)
    return links


def main():
    polys = read_polys(sys.argv[1])
    links = build_links(polys)
    out = bytearray(b'KFNV')
    out += struct.pack('<ii', 1, len(polys))
    for i, (_ti, vs, _ne) in enumerate(polys):
        out += struct.pack('<B', len(vs))
        for v in vs:
            out += struct.pack('<3f', *v)
        out += struct.pack('<H', len(links[i]))
        for other, (a, b) in sorted(links[i].items()):
            out += struct.pack('<i6f', other, *a, *b)
    with gzip.open(sys.argv[2], 'wb', compresslevel=9) as f:
        f.write(bytes(out))
    print(f'{sys.argv[1]}: {len(polys)} polygons, {sum(len(l) for l in links.values())} links, {len(out)} bytes')


if __name__ == '__main__':
    main()
