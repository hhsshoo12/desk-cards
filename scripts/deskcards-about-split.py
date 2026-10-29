exec(open('V:/deskcards-refactor.py', encoding='utf-8').read().split('# Config:')[0])
p='src/DeskCards/SettingsWindow.About.cs';s=read(p)
a=s.index('        var fill =');b=s.index('        var detail =',a);gauge=s[a:b]
s=s[:a]+'        var (gauge, fill, rest, percent) = UpdateGauge();\n\n'+s[b:]
a=s.index('        var icon = new Image');b=s.index('    /// <summary>Windows 11',a)
layout=s[a:b];layout=layout[:layout.rfind('    }')]
s=s[:a]+'        return AppCardLayout(detail, button);\n    }\n\n'+s[b:]
at=s.index('    /// <summary>Windows 11')
helpers='    private static (Grid Gauge, ColumnDefinition Fill, ColumnDefinition Rest, TextBlock Percent) UpdateGauge()\n    {\n'+gauge+'        return (gauge, fill, rest, percent);\n    }\n\n'
helpers+='    private Border AppCardLayout(Grid detail, Button button)\n    {\n'+layout+'    }\n\n'
s=s[:at]+helpers+s[at:];write(p,s)
