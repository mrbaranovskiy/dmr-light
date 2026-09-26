"""Freeze DMRA frame vectors from a separately compiled, pinned DSD-FME RC4/LFSR.

Development only: Python, GCC and network are needed to regenerate the fixture.
The downloaded C source remains under tmp; no native crypto runs in the receiver.
"""
import json
from pathlib import Path
import random
import subprocess
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
COMMIT = "fa4a33258209d2b02c1b9331340869f189582740"
BASE = f"https://raw.githubusercontent.com/lwvmobile/dsd-fme/{COMMIT}/"
WORK = ROOT / "tmp/arc4-reference"
WORK.mkdir(parents=True, exist_ok=True)


def fetch(name):
    text = urllib.request.urlopen(BASE + name).read().decode()
    (WORK / Path(name).name).write_text(text)
    return text


def function(source, name):
    start = source.index("void " + name + "(") if "void " + name + "(" in source else source.index("void " + name + " (")
    brace = source.index("{", start)
    depth = 1
    end = brace + 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]


rc4 = function(fetch("src/crypt-rc4.c"), "rc4_voice_decrypt")
lfsr = function(fetch("src/dmr_pi.c"), "LFSR")
fetch("COPYRIGHT")
source = r'''
#include <stdio.h>
#include <stdint.h>
#define KYEL ""
#define KNRM ""
typedef struct {
  int currentslot, payload_algid, payload_keyid, payload_algidR, payload_keyidR;
  unsigned long long payload_mi, payload_miR;
} dsd_state;
'''+rc4+'\n'+lfsr+r'''
int main(void) {
  char keyhex[11], inputhex[15]; unsigned mi, frame;
  while (scanf("%10s %x %u %14s", keyhex, &mi, &frame, inputhex)==4) {
    uint8_t key[9], input[7], output[7]; unsigned value;
    for(int n=0;n<5;n++) { sscanf(keyhex+2*n,"%2x",&value); key[n]=value; }
    dsd_state state={0}; state.payload_mi=mi;
    for(unsigned n=0;n<frame/18;n++) LFSR(&state);
    for(int n=0;n<4;n++) key[5+n]=(state.payload_mi>>(24-8*n))&255;
    for(int n=0;n<7;n++) { sscanf(inputhex+2*n,"%2x",&value); input[n]=value; }
    rc4_voice_decrypt(256+7*(frame%18),9,7,key,input,output);
    output[6]&=0x80;
    printf("%08llX ",state.payload_mi);
    for(int n=0;n<7;n++) printf("%02X",output[n]);
    puts("");
  }
}
'''
(WORK / "reference.c").write_text(source)
exe = WORK / "reference.exe"
subprocess.run(["gcc", "-O2", "-o", str(exe), str(WORK / "reference.c")], check=True)
rng = random.Random(473)
cases = []
for key_id, key, mi in [(7,"0102030405","12345678"), (12,"AAF00D195E","DEADBEEF"), (0,"0000000000","00000000")]:
    clear = [f"{rng.getrandbits(49)<<7:014X}" for _ in range(36)]
    inputs = "".join(f"{key} {mi} {n} {bits}\n" for n, bits in enumerate(clear))
    output = subprocess.run([str(exe)], input=inputs, text=True, capture_output=True, check=True).stdout.splitlines()
    assert len(output) == len(clear)
    cases.append(dict(key_id=key_id, key_hex=key, initial_mi=mi, frames=[
        dict(index=n, message_indicator=line.split()[0], ciphertext49=line.split()[1], plaintext49=bits)
        for n, (bits, line) in enumerate(zip(clear, output))]))
fixture = {"reference": {"dsd_fme_commit": COMMIT,
    "note": "Synthetic AMBE parameters, pinned C rc4_voice_decrypt and LFSR. Not a radio capture or speech synthesis test."}, "cases": cases}
(ROOT / "tests/fixtures/arc4_reference.json").write_text(json.dumps(fixture, indent=2) + "\n")
print("Wrote 108 DMRA ARC4 reference frames across three keys and two superframes each.")
