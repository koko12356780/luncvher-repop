"""Fetch only the Windows x64 release member of the official Godot template ZIP."""
from pathlib import Path
import io,struct,urllib.request,zipfile,zlib
url='https://github.com/godotengine/godot-builds/releases/download/4.6.1-stable/Godot_v4.6.1-stable_export_templates.tpz'
target=Path(__file__).resolve().parents[1]/'.ci'/'windows_release_x86_64.exe'
def get_range(start,end):
    bounds=str(start) if start<0 else f'{start}-{end}'
    req=urllib.request.Request(url,headers={'Range':f'bytes={bounds}','User-Agent':'OpenStreets-build'})
    with urllib.request.urlopen(req,timeout=120) as r:
        if r.status!=206: raise RuntimeError('Host did not honor partial archive request')
        return r.read(),r.headers
with urllib.request.urlopen(urllib.request.Request(url,method='HEAD'),timeout=60) as r:
    size=int(r.headers['Content-Length'])
tail,headers=get_range(size-65536,size-1)
e=tail.rfind(b'PK\x05\x06')
fields=struct.unpack_from('<4s4H2LH',tail,e)
cd_size,cd_offset=fields[5],fields[6]
central,_=get_range(cd_offset,cd_offset+cd_size-1)
pos=0
while pos<len(central):
    f=struct.unpack_from('<4s6H3L5H2L',central,pos)
    name=central[pos+46:pos+46+f[10]].decode('utf-8')
    if name=='templates/windows_release_x86_64.exe':
        compressed,uncompressed,offset,method,crc=f[8],f[9],f[16],f[4],f[7]
        print('Official template:',name,'compressed bytes:',compressed,flush=True)
        local,_=get_range(offset,offset+29)
        l=struct.unpack('<4s5H3L2H',local)
        start=offset+30+l[9]+l[10]
        data,_=get_range(start,start+compressed-1)
        data=zlib.decompress(data,-15) if method==8 else data
        assert len(data)==uncompressed and zlib.crc32(data)==crc
        target.write_bytes(data)
        import hashlib
        assert hashlib.sha256(data).hexdigest()=='6a0266cb7571aa4d437a32094acd353f020c77dcf7ff5a3305ae45d0609e5c20'
        print('Verified official runtime SHA256; saved',target,len(data),flush=True)
        break
    pos+=46+f[10]+f[11]+f[12]
else: raise RuntimeError('Windows template missing')
