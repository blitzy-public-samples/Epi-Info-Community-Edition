import re
SQLT={'AnsiString':'nvarchar','String':'nvarchar','AnsiStringFixedLength':'nchar','StringFixedLength':'nchar','Binary':'binary','Boolean':'bit','Byte':'tinyint','Currency':'money','Date':'datetime','DateTime':'datetime','Time':'datetime','Decimal':'decimal','Double':'float','Guid':'uniqueidentifier','Image':'image','Int16':'smallint','UInt16':'smallint','Int32':'int','UInt32':'int','Int64':'bigint','UInt64':'bigint','Object':'binary','SByte':'tinyint','Single':'real','StringLong':'ntext','VarNumeric':'decimal','Xml':'xml'}
def split_args(s):
    out=[];depth=0;cur='';q=False
    for ch in s:
        if ch=='"': q=not q
        if not q and ch=='(' : depth+=1
        if not q and ch==')' : depth-=1
        if ch==',' and depth==0 and not q: out.append(cur.strip()); cur=''
        else: cur+=ch
    if cur.strip(): out.append(cur.strip())
    return out
def parse(path):
    src=open(path,encoding='utf-8-sig').read()
    src=re.sub(r'(?m)^\s*//.*$','',src)  # drop commented-out C# lines
    m=re.search(r'private void CreateTables\(\)\s*\{(.*?)\}',src,re.S)
    order=re.findall(r'(Create\w+Table)\(\);',m.group(1))
    tables=[]
    for fn in order:
        b=re.search(r'void '+fn+r'\(\)\s*\{(.*?)\n        \}',src,re.S).group(1)
        name=re.search(r'db\.CreateTable\("(\w+)"',b).group(1)
        cols=[]
        for a in re.findall(r'new TableColumn\((.*?)\)\);',b):
            p=split_args(a); c={'name':p[0].strip('"'),'type':p[1].split('.')[-1],'len':None,'null':True,'pk':False,'ident':False,'fk':None,'cascade':False}
            rest=p[2:]
            if rest and re.fullmatch(r'\d+',rest[0]):
                c['len']=int(rest[0]); rest=rest[1:]
                if rest and re.fullmatch(r'\d+',rest[0]): c['len']=None; rest=rest[1:]  # precision/scale ignored by SqlDatabase
            c['null']=rest[0]=='true'; rest=rest[1:]
            if rest and rest[0].startswith('"'):
                c['fk']=(rest[0].strip('"'),rest[1].strip('"')); rest=rest[2:]
                if rest: c['cascade']=rest[0]=='true'
            else:
                if rest: c['pk']=rest[0]=='true'
                if len(rest)>1: c['ident']=rest[1]=='true'
            cols.append(c)
        tables.append((name,cols))
    return tables
def ddl(name,cols):
    parts=[]
    for c in cols:
        t=SQLT[c['type']]
        s='['+c['name']+'] '
        if t=='nvarchar' and c['len'] and c['len']>8000: s+='nvarchar(max)'
        else:
            s+=t
            if c['len'] and 0<c['len']<=8000: s+='(%d)'%c['len']
        s+=' NULL' if c['null'] else ' NOT NULL'
        if c['ident']: s+=' IDENTITY'
        if c['pk']: s+=' CONSTRAINT [PK_%s_%s] PRIMARY KEY'%(c['name'],name)
        if c['fk']:
            s+=' REFERENCES [%s]([%s])'%c['fk']
            if c['cascade']: s+=' ON DELETE CASCADE'
        parts.append(s)
    return 'CREATE TABLE [dbo].[%s] (\n    %s\n);'%(name,',\n    '.join(parts))
