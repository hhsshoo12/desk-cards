from pathlib import Path
import textwrap
root=Path('V:/desktop/deskfolders')
def read(p): return (root/p).read_text(encoding='utf-8-sig')
def write(p,s): (root/p).write_text(s,encoding='utf-8',newline='\r\n')
def dedent(s,n=4): return textwrap.indent(textwrap.dedent(s).strip('\n'),' '*n)+'\n'

# Config: preserve candidate order and the exception boundary around normalization.
p='src/DeskCards/Config.cs';s=read(p)
a=s.index('                    var positions =');b=s.index('                    return cfg;',a)
body=s[a:b];s=s[:a]+'                    Normalize(cfg);\n'+s[b:]
at=s.index('    public void Save()')
s=s[:at]+'    private static void Normalize(Config cfg)\n    {\n'+dedent(body,8)+'    }\n\n'+s[at:]
a=s.index('                if (!File.Exists(candidate))');b=s.index('                if (cfg != null)',a)
s=s[:a]+'                var cfg = ReadCandidate(candidate);\n'+s[b:]
at=s.index('    private static void Normalize')
s=s[:at]+'''    private static Config? ReadCandidate(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<Config>(File.ReadAllText(path)) : null;

'''+s[at:];write(p,s)

# Package: extraction keeps validation order, defaults and exception text.
p='src/DeskCards/DardPackage.cs';s=read(p)
a=s.index('        JsonElement m;');b=s.index('        return new DardPackage',a)
original=s[a:b]
j=original.index('        if (!m.TryGetProperty("dard"');c=original.index('        var cards =');r=original.index('        (double, double)? settingsRatio');t=original.index('        bool perCard')
helpers='    private static JsonElement ReadManifest(byte[] manifestBytes)\n    {\n'+original[:j]+'        return m;\n    }\n\n'
helpers+='    private static (string Id, string Name, string Version) ReadIdentity(JsonElement m)\n    {\n'+original[j:c]+'        return (id, name, version);\n    }\n\n'
helpers+='    private static List<DardCardInfo> ReadCards(JsonElement m, string name)\n    {\n'+original[c:r]+'        return cards;\n    }\n\n'
helpers+='    private static (double, double)? ReadSettingsRatio(JsonElement m, byte[]? settingsHtml)\n    {\n'+original[r:t]+'        return settingsRatio;\n    }\n\n'
helpers+='    private static bool ReadStorage(JsonElement m)\n    {\n'+original[t:]+'        return perCard;\n    }\n\n'
s=s[:a]+'''        var m = ReadManifest(manifestBytes);
        var (id, name, version) = ReadIdentity(m);
        var cards = ReadCards(m, name);
        var settingsRatio = ReadSettingsRatio(m, settingsHtml);
        bool perCard = ReadStorage(m);

'''+s[b:]
at=s.index('    /// <summary>비율');s=s[:at]+helpers+s[at:];write(p,s)

# Reconcile: removal and individual file processing, with busy flag unchanged.
p='src/DeskCards/GroupManager.Dards.cs';s=read(p)
a=s.index('        var present =');b=s.index('        bool busy = false;',a)
cleanup=s[a:b];s=s[:a]+'        ForgetMissingDards(files);\n\n'+s[b:]
a=s.index('        foreach (var path in files)');b=s.index('        _dardFilesBusy = busy;',a)
loop=s[a:b];body=loop[loop.index('        {\n')+10:loop.rfind('        }')]
body=dedent(body,8).replace('continue;', 'return;')
s=s[:a]+'        foreach (var path in files) ReconcileDard(path, ref busy);\n'+s[b:]
at=s.index('    /// <summary>승인했으면')
s=s[:at]+'    private void ForgetMissingDards(List<string> files)\n    {\n'+cleanup+'    }\n\n    private void ReconcileDard(string path, ref bool busy)\n    {\n'+body+'    }\n\n'+s[at:];write(p,s)

# Native message dispatch: isolate the two substantial handlers.
p='src/DeskCards/DeskCard.cs';s=read(p)
helpers=''
for start,end,name,args in [('            // 항상 다른 창 뒤','        else if (msg == Native.WM_ENTERSIZEMOVE)','KeepDesktopOrder','IntPtr lParam'),('            // 자유 이동 + 스마트 가이드','        else if (msg == Native.WM_EXITSIZEMOVE)','MoveWithGuides','IntPtr hwnd, IntPtr lParam')]:
    a=s.index(start);b=s.index(end,a);chunk=s[a:b];body=chunk[:chunk.rfind('        }')]
    if name=='MoveWithGuides':
        body=body[:body.index('            handled = true;')]
        replacement='            MoveWithGuides(hwnd, lParam);\n            handled = true;\n            return new IntPtr(1);\n'
    else: replacement='            KeepDesktopOrder(lParam);\n'
    s=s[:a]+replacement+'        }\n'+s[b:]
    helpers+='    private void '+name+'('+args+')\n    {\n'+dedent(body,8)+'    }\n\n'
at=s.index('    /// <summary>\n    /// 옮기기를 마쳤을 때');s=s[:at]+helpers+s[at:];write(p,s)

# Storage transfer: retain journal writes immediately after each successful stage.
p='src/DeskCards/DardStorage.Transfer.cs';s=read(p);helpers=''
for marker,nextmarker,name,args,call in [
('            if (!move.Exported)','            if (!move.Imported)','ExportMoveAsync','Keeper from, MoveRecord move, bool legacyProfile, bool fromNet, string id, string dir, string path','from, move, legacyProfile, fromNet, pkg.Id, dir, path'),
('            if (!move.Imported)','            var keep =','ImportMoveAsync','Keeper to, MoveRecord move, string dir, string path','to, move, dir, path'),
('            var keep =','            return move.Skipped;','ClearMoveSourceAsync','Keeper from, MoveRecord move, bool sameEnv, bool legacyProfile, bool fromNet','from, move, sameEnv, legacyProfile, fromNet')]:
    a=s.index(marker,s.index('public static async Task<int> MoveAsync'));b=s.index(nextmarker,a)
    body=s[a:b].replace('pairs','move.Origins').replace('pkg.Id','id')
    helpers+='\n    private static async Task '+name+'('+args+')\n    {\n'+dedent(body,8)+'    }\n'
    s=s[:a]+'            await '+name+'('+call+');\n'+s[b:]
s=s[:s.rfind('}')]+helpers+'}\n';write(p,s)

# Setup: keep rollback state mutations at their original boundaries.
p='installer/DeskCards.Setup/SetupEngine.cs';s=read(p)
s=s.replace('        bool backedUp = false, replaced = false, createdDirectory = false;\n        string versionPath = Path.Combine(_env.InstallDir, Shared.InstalledVersionFile.Name);\n        byte[]? oldVersion = null;\n        bool versionReplaced = false;','        var rollback = new InstallRollback();')
start=s.index('    public async Task InstallAsync');end=s.index('    private static void CopyDirectory')
part=s[start:end]
import re
for name in ['backedUp','replaced','createdDirectory','oldVersion','versionReplaced']:
    part=re.sub(r'\b'+name+r'\b','rollback.'+name,part)
part=part.replace('versionPath','Path.Combine(_env.InstallDir, Shared.InstalledVersionFile.Name)')
# Avoid collision between state variable and catch parameter.
part=part.replace('catch (Exception rollback)','catch (Exception recoveryError)').replace('_log.Write("복구 실패", rollback)','_log.Write("복구 실패", recoveryError)').replace('new AggregateException(ex, rollback)','new AggregateException(ex, recoveryError)')
helpers='''    private sealed class InstallRollback
    {
        public bool backedUp, replaced, createdDirectory, versionReplaced;
        public byte[]? oldVersion;
    }

'''
specs=[('            Step("다운로드");','            token.ThrowIfCancellationRequested();','DownloadAndVerifyAsync','AppRelease release, string temp, string stage, IProgress<TransferProgress>? progress, Action<string> Step, CancellationToken token','            string version = await DownloadAndVerifyAsync(release, temp, stage, progress, Step, token).ConfigureAwait(false);\n','async Task<string>'),
('            if (!Directory.Exists(_env.InstallDir))','            Step("제거기 복사");','ReplaceApp','string stage, string old, AppRelease release, InstallRollback rollback','            ReplaceApp(stage, old, release, rollback);\n','void'),
('            Step("제거기 복사");','            Step("정리");','RegisterInstall','string setupExe, InstallOptions options, string version, Action<string> Step','            RegisterInstall(setupExe, options, version, Step);\n','void'),
('                if (rollback.backedUp)','            }\n            catch (Exception recoveryError)','RollbackInstall','string old, InstallRollback rollback','                RollbackInstall(old, rollback);\n','void')]
for marker,nextmarker,name,args,call,ret in specs:
    a=part.index(marker);b=part.index(nextmarker,a);body=part[a:b]
    if name=='DownloadAndVerifyAsync': body+='            return version;\n'
    helpers+='    private '+ret+' '+name+'('+args+')\n    {\n'+dedent(body,8)+'    }\n\n'
    part=part[:a]+call+part[b:]
s=s[:start]+part+helpers+s[end:];write(p,s)
