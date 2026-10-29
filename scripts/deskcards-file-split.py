exec(open('V:/deskcards-refactor.py', encoding='utf-8').read().split('# Config:')[0])
def split(p,parts,decl):
    s=read(p);head=s[:s.index('namespace DeskCards;')+len('namespace DeskCards;')] if p.startswith('src/') else s[:s.index('internal static partial class Program')].rstrip()
    for suffix,ranges in parts:
        chunks=[]
        for start,end in ranges:
            a=s.index(start);b=s.index(end,a) if end else s.rfind('}')
            chunks.append(s[a:b]);s=s[:a]+s[b:]
        write(str(Path(p).with_name(Path(p).stem+'.'+suffix+'.cs')),head+'\n\n'+decl+'\n{\n'+''.join(chunks).rstrip()+'\n}\n')
    if 'DeskCard.cs' in p:s=s.replace('internal abstract class DeskCard : Window','internal abstract partial class DeskCard : Window')
    write(p,s)

split('src/DeskCards/GroupManager.cs',[
('Settings', [('    /// <summary>카드를 옮기거나 크기를','    /// <summary>편집 중이면 모든'),('    // ----- 카드 바 -----','    /// <summary>다른 카드들의 화면')]),
('Edit',[('    // ----- 편집 모드 -----','    public void Start()')]),
('Groups',[('    private void OnFolderRenamed','    private void RecreateCard'),('    private List<string>? ListGroupFolders','    public void Reconcile()'),('    /// <summary>빈 그룹을 만들고','    public void Shutdown()')]),
('Placement',[('    /// <summary>편집 중이면 모든','    /// <summary>다른 카드들의 화면'),('    /// <summary>다른 카드들의 화면','    public void Start()'),('    private void OnDisplaySettingsChanged','    private void RecreateCard'),('    /// <summary>저장된 자리','    private void OnCardClosed'),('    public void SavePosition','    public void Shutdown()'),('    private static bool IsOnScreen',None)])
], 'internal sealed partial class GroupManager')

split('src/DeskCards/DeskCard.cs',[
('Drag',[('    // ----- 입력 -----','    /// <summary>드롭 대상이거나'),('    private void MoveWithGuides','    // ----- 크기 / 위치 -----')]),
('Native',[('    // ----- 바탕화면 층에 붙이기 -----','    // ----- 크기 / 위치 -----')]),
('Placement',[('    // ----- 크기 / 위치 -----','    private void OnThemeChanged')])
], 'internal abstract partial class DeskCard')

split('tests/DeskCards.RegressionTests/Program.cs',[
('ConfigurationTests',[('    private static void ConfigurationTests','    private static void BarSettingsTests'),('    private static void ConfigurationRecoveryTests','    private static void GroupFileTests')]),
('BarSettingsTests',[('    private static void BarSettingsTests','    private static void GroupFileTests')]),
('GroupFileTests',[('    private static void GroupFileTests','    private static void CardWindowTests')]),
('CardWindowTests',[('    private static void CardWindowTests','    private static void SettingsLifecycleTests')]),
('SettingsLifecycleTests',[('    private static void SettingsLifecycleTests','    // ----- .dard')]),
('DardPackageTests',[('    // ----- .dard','    // ----- 앱 자체')]),
('UpdateTests',[('    // ----- 앱 자체','    private static void Check')]),
('TestSupport',[('    private static void Check','    private static void Test')])
], 'internal static partial class Program')
