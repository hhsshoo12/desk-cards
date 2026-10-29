from pathlib import Path
from collections import Counter
import subprocess
r=Path('V:/desktop/deskfolders')
def lines(s):
    return Counter(l for l in s.splitlines() if l.strip() and not l.startswith(('using ','namespace ','internal abstract ','internal sealed partial ','internal static partial ')) and l not in ('{','}'))
for p in ['src/DeskCards/DeskCard.cs','src/DeskCards/GroupManager.cs','tests/DeskCards.RegressionTests/Program.cs']:
    old=lines(subprocess.check_output(['git','show','HEAD:'+p],cwd=r).decode('utf-8-sig'))
    paths=[r/p]
    untracked=subprocess.check_output(['git','ls-files','--others','--exclude-standard'],cwd=r).decode().splitlines()
    paths += [r/f for f in untracked if f.startswith(str(Path(p).with_suffix('')).replace('\\','/')+'.')]
    new=Counter()
    for f in paths:new.update(lines(f.read_text(encoding='utf-8-sig')))
    assert new==old,(p,new-old,old-new)
    print(p+': moved member lines exactly preserved')
