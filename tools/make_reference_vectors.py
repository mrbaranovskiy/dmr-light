"""Generate independently decoded AMBE vectors using pinned upstream C mbelib.
Requires Python, GCC, and network access; used only to regenerate test fixtures.
No external native library is used by the receiver.
"""
import hashlib
import json
from pathlib import Path
import re
import subprocess
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
MBE = '9a04ed5c78176a9965f3d43f7aa1b1f5330e771f'
DSD = '59423fa46be8b41ef0bd2f3d2b45590600be29f0'
work = ROOT / 'tmp/reference/mbelib'
work.mkdir(parents=True, exist_ok=True)
def get(url):
    return urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent':'dmr-tests'})).read()
listing = json.loads(get(f'https://api.github.com/repos/szechyjs/mbelib/contents?ref={MBE}'))
for entry in listing:
    name = entry['name']
    if name.endswith(('.c','.h')):
        (work/name).write_bytes(get(f'https://raw.githubusercontent.com/szechyjs/mbelib/{MBE}/{name}'))
mapping = get(f'https://raw.githubusercontent.com/szechyjs/dsd/{DSD}/include/dmr_const.h').decode()
(work/'dmr_const.h').write_text(mapping)
(work/'reference_main.c').write_text(r'''
#include <stdio.h>
#include <string.h>
#include "mbelib.h"
#define _MAIN
#include "dmr_const.h"
int main(void) {
  char hex[32];
  while (scanf("%18s", hex)==1) {
    char frame[4][24]={{0}}, payload[49]={0}; unsigned char air[9], out[7]={0};
    for(int i=0;i<9;i++) { unsigned int v; sscanf(hex+2*i,"%2x",&v); air[i]=(unsigned char)v; }
    for(int i=0;i<36;i++) {
      frame[rW[i]][rX[i]]=(air[(2*i)/8]>>(7-(2*i)%8))&1;
      frame[rY[i]][rZ[i]]=(air[(2*i+1)/8]>>(7-(2*i+1)%8))&1;
    }
    mbe_eccAmbe3600x2450C0(frame);
    mbe_demodulateAmbe3600x2450Data(frame);
    mbe_eccAmbe3600x2450Data(frame,payload);
    for(int i=0;i<49;i++) out[i/8]|=payload[i]<<(7-i%8);
    for(int i=0;i<7;i++) printf("%02X",out[i]);
    printf("\n");
  }
  return 0;
}
''')
exe=work/'reference.exe'
subprocess.run(['gcc','-O2','-o',str(exe),*[str(x) for x in work.glob('*.c')],'-lm'],check=True)
events = [json.loads(line) for line in (ROOT/'output/dmr_test.jsonl').read_text().splitlines()]
raw = [e['data']['payload_hex'] for e in events if e['type']=='vocoder_frame'][:24]
decoded=subprocess.run([str(exe)],input='\n'.join(raw)+'\n',text=True,capture_output=True,check=True).stdout.splitlines()
assert len(decoded)==len(raw)
vectors=[{'air72':air,'payload49':payload} for air,payload in zip(raw,decoded)]
out={'reference':{'mbelib_commit':MBE,'dsd_commit':DSD,
                 'capture_sha256':hashlib.sha256((ROOT/'dmr_test.wav').read_bytes()).hexdigest(),
                 'note':'Channel-decoding vectors from opaque vendor traffic, not clear speech or proof of voice integrity.'},
     'vectors':vectors}
(ROOT/'tests/fixtures').mkdir(parents=True,exist_ok=True)
(ROOT/'tests/fixtures/ambe_reference.json').write_text(json.dumps(out,indent=2)+'\n')
print(f'Wrote {len(vectors)} vectors decoded by pinned C mbelib.')
