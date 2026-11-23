"""Builds /testdata SQL Server seed from the Epi Info sample Access projects (Lyme, EColi).
Schema follows the legacy SQL Server code paths:
  meta tables  -> Epi.Core/Data/Services/MetadataDbProvider.cs CreateTables()
  data tables  -> Epi.Core/Data/Services/CollectedDataProvider.cs CreateDataTableForView / CreatePageDataTableForView
  column types -> field GetDbSpecificColumnType() + Epi.Data.SqlServer/SqlDatabase.cs type map
  code tables  -> MetadataDbProvider.CreateCodeTable (lowercase name, nvarchar(255) NOT NULL)
Usage: python3 build_testdata.py <repo_root>"""
import sys, os, json, datetime, uuid, collections
HERE=os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0,HERE)
import jet4, csmeta
REPO=os.path.abspath(sys.argv[1]); OUT=os.path.join(REPO,'testdata'); SQL=os.path.join(OUT,'sql')
os.makedirs(SQL,exist_ok=True)
DBNAME='EpiRehearsal'
PROJ=os.path.join(REPO,'Epi.Core','Projects')
META=csmeta.parse(os.path.join(REPO,'Epi.Core','Data','Services','MetadataDbProvider.cs'))
METACOLS={n:[c['name'] for c in cs] for n,cs in META}
IDENT={n:[c['name'] for c in cs if c['ident']] for n,cs in META}

# ---- field type -> SQL Server column type (as legacy CreateTableColumn produces it)
FT_SQL={1:'nvarchar(4000)',3:'nvarchar(4000)',4:'ntext',5:'float',6:'nvarchar(20)',7:'datetime',8:'datetime',9:'datetime',
        10:'bit',11:'tinyint',12:'smallint',17:'nvarchar(255)',18:'nvarchar(255)',19:'nvarchar(255)',25:'uniqueidentifier'}
SYSTEM_FT={22,23,24,26}
def lit(v):
    if v is None: return 'NULL'
    if isinstance(v,bool): return '1' if v else '0'
    if isinstance(v,(int,)): return str(v)
    if isinstance(v,float): return repr(v)
    if isinstance(v,datetime.datetime): return "'"+v.strftime('%Y-%m-%dT%H:%M:%S')+"'"
    if isinstance(v,(bytes,bytearray)): return '0x'+bytes(v).hex() if v else 'NULL'
    return "N'"+str(v).replace("'","''")+"'"
def ci(r,c):
    if c in r: return r[c]
    for k in r:
        if k.lower()==c.lower(): return r[k]
    return None
def inserts(table, rows, cols, ident=False, batch=200):
    out=[]
    if not rows: return out
    if ident: out.append('SET IDENTITY_INSERT [dbo].[%s] ON;'%table)
    colsql=', '.join('[%s]'%c for c in cols)
    for i in range(0,len(rows),batch):
        chunk=rows[i:i+batch]
        out.append('INSERT INTO [dbo].[%s] (%s) VALUES\n'%(table,colsql)+',\n'.join('('+', '.join(lit(ci(r,c)) for c in cols)+')' for r in chunk)+';')
    if ident: out.append('SET IDENTITY_INSERT [dbo].[%s] OFF;'%table)
    out.append('GO')
    return out

# ---- load sources and remap ids into one project
SOURCES=[('Lyme/Lyme.mdb',{'views':{1:1},'pages':{1:1},'field_off':0,'img_off':0,'bg_off':0}),
         ('EColi/EColi.mdb',{'views':{1:2},'pages':{1:2,2:3},'field_off':1000,'img_off':3,'bg_off':1})]
meta=collections.defaultdict(list); codetables={}; forms=[]; datarows=collections.defaultdict(list); report=[]
for path,m in SOURCES:
    db=jet4.Jet4(os.path.join(PROJ,path)); T=lambda n: db.table(n)[1]
    keepviews=m['views']
    if not meta['metaDataTypes']:
        for t in ['metaDataTypes','metaFieldTypes','metaPatterns','metaLayerRenderTypes']: meta[t]=T(t)
    elif len(T('metaPatterns'))>len(meta['metaPatterns']): meta['metaPatterns']=T('metaPatterns')
    imgs={}
    for r in T('metaImages'): r=dict(r); r['ImageId']+=m['img_off']; meta['metaImages'].append(r)
    for r in T('metaBackgrounds'):
        r=dict(r); r['BackgroundId']+=m['bg_off']; r['ImageId']=r['ImageId']+m['img_off'] if r['ImageId'] else r['ImageId']; meta['metaBackgrounds'].append(r)
    views={r['ViewId']:r for r in T('metaViews')}
    pages=[r for r in T('metaPages') if r['ViewId'] in keepviews]
    fields=[r for r in T('metaFields') if r['ViewId'] in keepviews]
    pagetab={}
    for vid,nvid in keepviews.items():
        v=dict(views[vid]); v['ViewId']=nvid; meta['metaViews'].append(v)
        name=views[vid]['Name']
        for p in [p for p in pages if p['ViewId']==vid]:
            pagetab[name+str(p['PageId'])]=name+str(m['pages'][p['PageId']])
    for p in pages:
        p=dict(p); p['PageId']=m['pages'][p['PageId']]; p['ViewId']=keepviews[p['ViewId']]
        if p['BackgroundId']: p['BackgroundId']+=m['bg_off']
        meta['metaPages'].append(p)
    for f in fields:
        f=dict(f); f['FieldId']+=m['field_off']; f['ViewId']=keepviews[f['ViewId']]
        if f['PageId']: f['PageId']=m['pages'][f['PageId']]
        if f['RelatedViewId']: f['RelatedViewId']=keepviews.get(f['RelatedViewId'])
        if f['SourceFieldId']: f['SourceFieldId']+=m['field_off']
        if f['DataTableName'] in pagetab: f['DataTableName']=pagetab[f['DataTableName']]
        meta['metaFields'].append(f)
    for r in T('metaLinks'):
        if r['FromViewId'] in keepviews and r['ToViewId'] in keepviews:
            r=dict(r); r['FromViewId']=keepviews[r['FromViewId']]; r['ToViewId']=keepviews[r['ToViewId']]; r['LinkId']=None; meta['metaLinks'].append(r)
    if not meta['metaDbInfo']:
        meta['metaDbInfo']=[{'ProjectId':'6b1f4c2e-7d3a-4e11-9a5b-2c0e8f4d9a01','ProjectLocation':'(sql server)','ProjectName':DBNAME,'EpiVersion':'7.2.0.0','Purpose':0}]
    for k in [k for k in db.catalog if k.lower().startswith('code')]:
        cols,rows=db.table(k)
        codetables.setdefault(k.lower(),([c.name for c in cols],rows))
    # data tables
    for vid,nvid in keepviews.items():
        name=views[vid]['Name']
        bcols,brows=db.table(name)
        datarows[name]=brows
        vfields=[f for f in meta['metaFields'] if f['ViewId']==nvid]
        form={'viewId':nvid,'name':name,'checkCode':views[vid]['CheckCode'],'source':path,'pages':[]}
        for p in sorted([p for p in meta['metaPages'] if p['ViewId']==nvid],key=lambda p:p['Position']):
            tname=name+str(p['PageId'])
            src=[k for k,v in pagetab.items() if v==tname][0]
            pf=[f for f in vfields if f['PageId']==p['PageId'] and f['FieldTypeId'] in FT_SQL]
            pf.sort(key=lambda f:f['FieldId'])
            scols,srows=db.table(src)
            have={c.name for c in scols}
            missing=[f['Name'] for f in pf if f['Name'] not in have]; extra=[c for c in have if c!='GlobalRecordId' and c not in {f['Name'] for f in pf}]
            report.append('%s: %s <- %s  fields=%d rows=%d missing_in_source=%s extra_in_source=%s'%(path,tname,src,len(pf),len(srows),missing,extra))
            datarows[tname]=srows
            form['pages'].append({'pageId':p['PageId'],'name':p['Name'],'table':tname,'fields':[{
                'name':f['Name'],'fieldTypeId':f['FieldTypeId'],'sqlType':FT_SQL[f['FieldTypeId']],'prompt':f['PromptText'],
                'required':bool(f['IsRequired']),'lower':f['Lower'],'upper':f['Upper'],'pattern':f['Pattern'],
                'codeTable':(f['SourceTableName'] or '').lower() or None,'codeColumn':f['TextColumnName'] or f['CodeColumnName']} for f in pf]})
        forms.append(form)

# ---- verify base/page joins
for f in forms:
    base={r['GlobalRecordId'] for r in datarows[f['name']]}
    for p in f['pages']:
        pg={r['GlobalRecordId'] for r in datarows[p['table']]}
        report.append('join %s/%s: base=%d page=%d orphan_page=%d base_without_page=%d'%(f['name'],p['table'],len(base),len(pg),len(pg-base),len(base-pg)))

# ---- validate meta rows against legacy column definitions (length, NOT NULL)
COLDEF={n:{c['name'].lower():c for c in cs} for n,cs in META}
for t,rows in meta.items():
    if t not in COLDEF: continue
    for r in rows:
        for cname,c in COLDEF[t].items():
            v=ci(r,c['name'])
            if v is None and not c['null'] and not c['ident']: report.append('VIOLATION %s.%s NOT NULL'%(t,c['name']))
            if isinstance(v,str) and c['len'] and c['type'] in ('String','AnsiString') and len(v)>c['len']: report.append('VIOLATION %s.%s len %d > %d'%(t,c['name'],len(v),c['len']))
            if c['type']=='Decimal' and c['len'] and isinstance(v,(int,float)) and abs(v)>=10**c['len']: report.append('VIOLATION %s.%s decimal(%d) overflow %s'%(t,c['name'],c['len'],v))
files={}
# 00 database
files['00_create_database.sql']=["-- Drops and recreates the rehearsal database. Safe to rerun; it resets all test data.",
 "USE master;","GO","IF DB_ID(N'%s') IS NOT NULL BEGIN ALTER DATABASE [%s] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [%s]; END"%(DBNAME,DBNAME,DBNAME),"GO","CREATE DATABASE [%s];"%DBNAME,"GO"]
use=['USE [%s];'%DBNAME,'GO']
# 01 metadata schema
files['01_metadata_schema.sql']=['-- Epi Info metadata tables, generated from MetadataDbProvider.CreateTables() (SQL Server path).']+use+[csmeta.ddl(n,c)+'\nGO' for n,c in META]
# 02 reference data
L=['-- Lookup rows Epi Info inserts in code (not in MetadataTableScript.sql). Copied from the sample projects.']+use
for t in ['metaDataTypes','metaFieldTypes','metaPatterns','metaLayerRenderTypes']:
    L+=inserts(t,meta[t],METACOLS[t],ident=bool(IDENT[t]))
files['02_reference_data.sql']=L
# 03 forms
L=['-- Project + form definitions: CaseReport (Lyme, view 1, page 1) and FoodHistory (EColi, view 2, pages 2-3).']+use
for t in ['metaDbInfo','metaImages','metaBackgrounds','metaViews','metaPages','metaFields']:
    L+=inserts(t,meta[t],METACOLS[t],ident=bool(IDENT[t]),batch=50 if t=='metaImages' else 200)
files['03_forms.sql']=L
# 04 code tables
L=['-- Code (lookup) tables used by legal values / comment legal fields.']+use
for k,(cols,rows) in sorted(codetables.items()):
    L.append('CREATE TABLE [dbo].[%s] (%s);'%(k,', '.join('[%s] nvarchar(255) NOT NULL'%c for c in cols))); L.append('GO')
    L+=inserts(k,rows,cols)
files['04_code_tables.sql']=L
# 05 data table schema
L=['-- Collected data tables as CollectedDataProvider creates them on SQL Server.','-- Base table per form + one table per page named <Form><PageId>, joined on GlobalRecordId.']+use
for f in forms:
    L.append('''CREATE TABLE [dbo].[%s] (
    [UniqueKey] int IDENTITY(1,1) PRIMARY KEY NOT NULL,
    [RecStatus] smallint NOT NULL DEFAULT 1,
    [GlobalRecordId] nvarchar(255) NOT NULL,
    [FirstSaveLogonName] nvarchar(255),
    [FirstSaveTime] datetime,
    [LastSaveLogonName] nvarchar(255),
    [LastSaveTime] datetime,
    [FKEY] nvarchar(255)
);
GO'''%f['name'])
    for p in f['pages']:
        L.append('CREATE TABLE [dbo].[%s] (\n    [GlobalRecordId] nvarchar(255) PRIMARY KEY NOT NULL%s\n);\nGO'%(p['table'],''.join(',\n    [%s] %s'%(x['name'],x['sqlType']) for x in p['fields'])))
files['05_data_tables.sql']=L
# 06 sample records
BASECOLS=['UniqueKey','RecStatus','GlobalRecordId','FirstSaveLogonName','FirstSaveTime','LastSaveLogonName','LastSaveTime','FKEY']
L=['-- Sample records shipped with Epi Info (CDC public-domain sample data; forms state all data is fictional).']+use
for f in forms:
    L+=inserts(f['name'],datarows[f['name']],BASECOLS,ident=True)
    for p in f['pages']:
        L+=inserts(p['table'],datarows[p['table']],['GlobalRecordId']+[x['name'] for x in p['fields']])
L+=inserts('metaLinks',meta['metaLinks'],[c for c in METACOLS['metaLinks'] if c!='LinkId'])
files['06_sample_records.sql']=L
for n,lines in files.items():
    open(os.path.join(SQL,n),'w',encoding='utf-8').write('\n'.join(lines)+'\n')
json.dump({'database':DBNAME,'forms':forms,'codeTables':{k:[list(r.values())[0] for r in v[1]] for k,v in codetables.items()}},
          open(os.path.join(OUT,'forms.json'),'w'),indent=2,default=str)
open(os.path.join(OUT,'build_report.txt'),'w').write('\n'.join(report)+'\n')
print('\n'.join(report))
for n in files: print(n, os.path.getsize(os.path.join(SQL,n)))
