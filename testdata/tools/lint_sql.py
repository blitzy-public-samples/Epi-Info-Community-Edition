"""Offline structural check of testdata/sql (no SQL Server needed): batches, quotes, CREATE TABLE
column sets, INSERT column names vs table, value counts per row, literal kinds vs column types."""
import re, os, sys, glob
HERE=os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
tables={}; errs=[]; n_ins=0; n_rows=0
def tokens_values(s,i):
    """parse ( v, v, ... ) starting at s[i]=='(' ; returns list of literal kinds and next index"""
    assert s[i]=='('; i+=1; vals=[]; cur=''
    while True:
        c=s[i]
        if c=="'" or (c=='N' and s[i+1]=="'"):
            j=i+1 if c=="'" else i+2
            while True:
                k=s.index("'",j)
                if k+1<len(s) and s[k+1]=="'": j=k+2; continue
                break
            vals.append('date' if c=="'" else 'str'); i=k+1; continue
        if c in ',)':
            t=cur.strip()
            if t: vals.append('null' if t=='NULL' else 'hex' if t.startswith('0x') else 'num' if re.fullmatch(r'-?\d+(\.\d+)?(e-?\d+)?',t) else 'BAD:'+t[:20])
            cur=''
            if c==')': return vals,i+1
            i+=1; continue
        cur+=c; i+=1
OK={'nvarchar':{'str','null'},'nchar':{'str','null'},'ntext':{'str','null'},'uniqueidentifier':{'str','null'},'datetime':{'date','null'},
    'bit':{'num','null'},'tinyint':{'num','null'},'smallint':{'num','null'},'int':{'num','null'},'bigint':{'num','null'},'float':{'num','null'},
    'real':{'num','null'},'decimal':{'num','null'},'money':{'num','null'},'image':{'hex','null'},'binary':{'hex','null'},'xml':{'str','null'}}
for f in sorted(glob.glob(os.path.join(HERE,'sql','*.sql'))):
    s=open(f,encoding='utf-8').read()
    if s.count("'")%2: errs.append((f,'odd quote count'))
    for m in re.finditer(r'CREATE TABLE \[dbo\]\.\[(\w+)\] \((.*?)\n?\);',s,re.S):
        cols={}
        for line in re.split(r',\s*\n',m.group(2)) if '\n' in m.group(2) else m.group(2).split(', ['):
            mm=re.search(r'\[?(\w+)\]? (\w+)',line.strip().lstrip('['))
            if mm: cols[mm.group(1).lower()]=mm.group(2).lower()
        tables[m.group(1).lower()]=cols
    for m in re.finditer(r'INSERT INTO \[dbo\]\.\[(\w+)\] \((.*?)\) VALUES\n',s):
        t=m.group(1).lower(); cols=[c.strip(' []').lower() for c in m.group(2).split(',')]; n_ins+=1
        if t not in tables: errs.append((os.path.basename(f),t,'table not created before insert')); continue
        for c in cols:
            if c not in tables[t]: errs.append((os.path.basename(f),t,c,'unknown column'))
        i=m.end()
        while True:
            vals,i=tokens_values(s,i); n_rows+=1
            if len(vals)!=len(cols): errs.append((os.path.basename(f),t,'value count',len(vals),len(cols))); break
            for c,v in zip(cols,vals):
                ty=tables[t].get(c)
                if ty and v not in OK.get(ty,{v}): errs.append((os.path.basename(f),t,c,ty,v))
            if s[i]==';': break
            assert s[i:i+2]==',\n', s[i:i+20]; i+=2
print('tables',len(tables),'insert statements',n_ins,'rows',n_rows,'errors',len(errs))
for e in errs[:30]: print(e)
sys.exit(1 if errs else 0)
