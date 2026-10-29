exec(open('V:/deskcards-refactor.py', encoding='utf-8').read().split('# Config:')[0])
p='tests/DeskCards.RegressionTests/Program.cs';s=read(p)
names=[('null position entries are discarded','ConfigurationTests'),('card bar edge is chosen per display','BarSettingsTests'),('non-finite zoom is normalized','ConfigurationRecoveryTests'),('ancestor directory drop is rejected','GroupFileTests'),('grid expansion stays inside monitor work area','CardWindowTests'),('settings, edit bar, and expanded window lifecycle','SettingsLifecycleTests')]
start=s.index('        Test("'+names[0][0]);end=s.index('        DwmCardTests(root);',start)
body=s[start:end];helpers='';calls=''
for i,(first,name) in enumerate(names):
    a=body.index('        Test("'+first);b=body.index('        Test("'+names[i+1][0]) if i+1<len(names) else len(body)
    args='string root, Application app' if name in ['CardWindowTests','SettingsLifecycleTests'] else 'string root'
    calls+='        '+name+'(root'+(', app' if 'Application' in args else '')+');\n'
    helpers+='    private static void '+name+'('+args+')\n    {\n'+body[a:b]+'    }\n\n'
s=s[:start]+calls+s[end:];at=s.index('    // ----- .dard');s=s[:at]+helpers+s[at:];write(p,s)
