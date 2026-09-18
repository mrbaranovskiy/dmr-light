"""Regenerate systematic parity rows directly from ETSI tables B.11-B.17.
Development-only dependency: pymupdf. Runtime is entirely managed C#.
"""
from pathlib import Path
import re
import pymupdf

root = Path(__file__).resolve().parents[1]
doc = pymupdf.open(root / 'ts_10236101v020101p.pdf')
tables = [('Golay20',133,20,8,3), ('Qr16',133,16,7,2),
          ('Hamming17',134,17,12,1), ('Hamming13',134,13,9,1),
          ('Hamming15',134,15,11,1), ('Hamming16',135,16,11,1),
          ('Hamming7',135,7,4,1)]
out = ['// Generated from ETSI TS 102 361-1 V2.1.1 tables B.11-B.17.',
       '// Regenerate with tools/generate_spec_tables.py; do not hand-edit.',
       'namespace Dmr.Fec;', '', 'public static class Codes', '{']
for name, page, n, k, t in tables:
    rows = []
    for line in doc[page-1].get_text(sort=True).splitlines():
        values = line.split()
        if len(values) == n and all(v in ('0','1') for v in values):
            rows.append(''.join(values))
    assert len(rows) == k, (name, rows)
    for i, row in enumerate(rows):
        assert int(row[:k],2) == 1 << (k-1-i)
    parity = ', '.join('0x%Xu' % int(row[k:],2) for row in rows)
    out.append(f'    public static readonly LinearCode {name} = new({n}, {k}, {t}, [{parity}]);')
out += ['}', '']
(root / 'src/Dmr/Fec/Codes.cs').write_text('\n'.join(out))
print('Generated seven checked ETSI matrices.')
