from pathlib import Path
import hashlib,json,os,re,shutil,subprocess,sys,urllib.request,zipfile
ROOT=Path(__file__).resolve().parents[1];os.chdir(ROOT)
GAME_REPO='koko12356780/open-world-sanboxcgame'
RUNTIME_HASH='6a0266cb7571aa4d437a32094acd353f020c77dcf7ff5a3305ae45d0609e5c20'
def version():
    value=re.search(r'AssemblyVersion\("([0-9.]+)"\)',Path('AssemblyInfo.cs').read_text())[1]
    return value,'v'+value.rsplit('.',1)[0]
def check():
    value,tag=version();r=subprocess.run(['gh','release','view',tag,'--repo',os.environ['GITHUB_REPOSITORY']],capture_output=True)
    with open(os.environ['GITHUB_OUTPUT'],'a',encoding='utf8') as f:f.write(f'tag={tag}\nexists={str(r.returncode==0).lower()}\n')
    print('Release already exists; increase AssemblyInfo version.' if r.returncode==0 else 'Building launcher '+value)
def fetch():
    ci=Path('.ci');ci.mkdir(exist_ok=True)
    request=urllib.request.Request(f'https://github.com/{GAME_REPO}/releases/latest/download/latest.json',headers={'User-Agent':'OpenStreets-Builder'})
    with urllib.request.urlopen(request,timeout=60) as r:feed=json.load(r)
    assert feed['game']=='Open Streets' and feed['zip_url'].startswith(f'https://github.com/{GAME_REPO}/releases/download/')
    archive=ci/'game.zip';urllib.request.urlretrieve(feed['zip_url'],archive)
    assert archive.stat().st_size==feed['size'] and hashlib.sha256(archive.read_bytes()).hexdigest()==feed['sha256']
    game=ci/'Game';game.mkdir(exist_ok=True)
    with zipfile.ZipFile(archive) as z:
        for name in ['OpenStreets.pck','GAME-LICENSE.txt','ENGINE-LICENSES.txt']:(game/name).write_bytes(z.read('Game/'+name))
        info=json.loads(z.read('update-info.json'));assert info['version']==feed['version']
    (ci/'game-info.json').write_text(json.dumps(info),encoding='utf8')
    subprocess.run([sys.executable,'ci/fetch_windows_template.py'],check=True)
    assert hashlib.sha256((ci/'windows_release_x86_64.exe').read_bytes()).hexdigest()==RUNTIME_HASH
def build():
    info=json.loads(Path('.ci/game-info.json').read_text())
    subprocess.run(['powershell','-NoProfile','-File','build.ps1','-GameFolder',str(ROOT/'.ci/Game'),'-OfficialRuntime',str(ROOT/'.ci/windows_release_x86_64.exe'),'-Destination',str(ROOT/'build/launcher'),'-GameVersion',info['version']],check=True)
    result=subprocess.run([str(ROOT/'build/launcher/LauncherTests.exe'),'--verify'],timeout=240)
    print(Path('build/launcher/portable-tests.log').read_text(encoding='utf8',errors='replace'))
    assert result.returncode==0
def package():
    value,tag=version();folder=Path('build/launcher');archive=Path('build/OpenStreetsLauncher-update.zip')
    with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
        z.write(folder/'OpenStreetsLauncher.exe','OpenStreetsLauncher.exe');z.write('LICENSE','LICENSE.txt')
        z.writestr('launcher-info.json',json.dumps({'game':'Open Streets Launcher','version':value}))
    feed={'game':'Open Streets Launcher','version':value,'zip_url':f'https://github.com/{os.environ["GITHUB_REPOSITORY"]}/releases/download/{tag}/OpenStreetsLauncher-update.zip','sha256':hashlib.sha256(archive.read_bytes()).hexdigest(),'size':archive.stat().st_size,'notes':'One-button GitHub game and launcher updates.'}
    Path('build/launcher-latest.json').write_text(json.dumps(feed,indent=2)+'\n',encoding='utf8')
    gameinfo=json.loads(Path('.ci/game-info.json').read_text())
    files=['OpenStreets.exe','OpenStreets.pck','GAME-LICENSE.txt','ENGINE-LICENSES.txt']
    receipt={'Version':gameinfo['version'],'Files':{name:hashlib.sha256((folder/'Game'/name).read_bytes()).hexdigest() for name in files}}
    with zipfile.ZipFile('build/OpenStreets-Full-Game.zip','w',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
        z.write(folder/'OpenStreetsLauncher.exe','OpenStreets/OpenStreetsLauncher.exe');z.write('LICENSE','OpenStreets/LICENSE.txt')
        for name in files:z.write(folder/'Game'/name,'OpenStreets/Game/'+name)
        z.writestr('OpenStreets/Game/release.json',json.dumps(receipt))
        z.writestr('OpenStreets/update-source.json',json.dumps({'Url':f'https://github.com/{GAME_REPO}/releases/latest/download/latest.json','CheckOnStart':True}))
        z.writestr('OpenStreets/READ-ME.txt','Extract the complete folder and open OpenStreetsLauncher.exe. Keep Game beside it. Press Update Game or Update Launcher to get the newest version directly from GitHub. Saved progress stays separate. No ZIP selecting/uploading is needed for future updates.\n')
    for p in [archive,Path('build/OpenStreets-Full-Game.zip')]:
        with zipfile.ZipFile(p) as z:assert z.testzip() is None
    print('Launcher and full-game release assets ready.')
if __name__=='__main__':
    action=sys.argv[1] if len(sys.argv)>1 else 'build'
    if action=='check':check()
    elif action=='package':package()
    else:fetch();build();package()
