"""Generates deterministic synthetic records for every form in forms.json -> sql/07_synthetic_records.sql.
Values follow field types, code tables and range limits, and apply each form's check code the way
legacy data entry would (so records look like they were entered through Epi Info Enter).
Usage: python3 generate_synthetic.py [--count 250] [--seed 20261123]"""
import json, os, sys, random, uuid, datetime, argparse
HERE=os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0,os.path.join(HERE,'tools'))
from epi_functions import years, epiweek
ap=argparse.ArgumentParser(); ap.add_argument('--count',type=int,default=250); ap.add_argument('--seed',type=int,default=20261123)
a=ap.parse_args(); rng=random.Random(a.seed)
F=json.load(open(os.path.join(HERE,'forms.json')))
FIRST=['Avery','Jordan','Riley','Casey','Morgan','Quinn','Rowan','Skyler','Emerson','Harper','Logan','Parker','Reese','Sawyer','Tatum','Blake']
LAST=['Testcase','Sample','Example','Fixture','Mockley','Placeholder','Synthetic','Dataman','Rehearsal','Seedling']
CITY=['Springfield','Riverton','Lakeside','Fairview','Georgetown','Franklin']
def uid(): return str(uuid.UUID(int=rng.getrandbits(128),version=4))
def d(s): return datetime.datetime.strptime(s,'%m-%d-%Y')
def rdate(lo,hi): return lo+datetime.timedelta(days=rng.randint(0,(hi-lo).days))
def code(f):
    vals=F['codeTables'].get(f['codeTable'] or '',[])
    if not vals: return None
    v=rng.choice(vals)
    return v.split('-')[0].strip() if f['fieldTypeId']==19 else v   # comment legal stores the code part
def value(f,rec):
    n=f['name'].lower(); t=f['fieldTypeId']
    if t in (17,18,19): return code(f)
    if t==10: return rng.random()<0.3
    if t==11: return rng.choice([0,1])
    if t==12: return rng.randint(0,2)
    if t==25: return uid()
    if t in (7,8,9):
        lo=d(f['lower']) if f.get('lower') else datetime.datetime(2010,1,1)
        hi=d(f['upper']) if f.get('upper') else datetime.datetime(2012,12,31)
        if n in ('dob',): lo,hi=datetime.datetime(1935,1,1),datetime.datetime(2009,12,31)
        return rdate(lo,hi)
    if t==5:
        if f.get('lower') not in (None,'') and f.get('upper') not in (None,''):
            return round(rng.uniform(float(f['lower']),float(f['upper'])),1)
        if n=='age': return float(rng.randint(1,90))
        if n=='latitude': return round(rng.uniform(33.4,33.8),3)
        if n=='longitude': return round(rng.uniform(-84.0,-83.1),3)
        if n.endswith('days'): return float(rng.randint(1,28))
        return float(rng.randint(0,100))
    if t==6: return '(555) 01%02d-%04d'%(rng.randint(0,99),rng.randint(0,9999))
    if t==4: return 'Synthetic note %d for %s.'%(rng.randint(1,9999),f['name'])
    if t in (1,3):
        if 'last' in n: v=rng.choice(LAST)
        elif 'first' in n: v=rng.choice(FIRST)
        elif 'email' in n: v='%s.%s@example.com'%(rng.choice(FIRST).lower(),rng.choice(LAST).lower())
        elif 'city' in n: v=rng.choice(CITY)
        elif 'zip' in n: v='%05d'%rng.randint(10000,99999)
        elif 'address' in n: v='%d Example St'%rng.randint(1,999)
        elif n=='state': v=rng.choice(['GA','SC','NC','TN','AL'])
        else: v='Synthetic %s'%f['name']
        return v.upper() if t==3 else v
    return None
def apply_check_code(form,rec):
    """Legacy check code effects, so stored data matches what Enter would have saved."""
    if form['name']=='CaseReport':
        # Field Sex After / Field LastName Before: IF Sex = "1" THEN ENABLE Pregnant ELSE DISABLE + CLEAR Pregnant
        # NOTE legacy defect: codeSex "1" = Male, so Pregnant is only enterable for males. Reproduced on purpose.
        if rec.get('Sex')!='1': rec['Pregnant']=None
    if form['name']=='FoodHistory':
        if rec.get('DOB') and rec.get('DateofInterview') and rec['DOB']>rec['DateofInterview']:
            rec['DOB']=rec['DateofInterview']-datetime.timedelta(days=rng.randint(365,365*80))
        rec['Age']=None if not (rec.get('DOB') and rec.get('DateofInterview')) else float(years(rec['DOB'],rec['DateofInterview']))
        if rec.get('OnsetDate') and rec.get('DateofInterview') and rec['OnsetDate']>rec['DateofInterview']:
            rec['OnsetDate']=rec['DateofInterview']-datetime.timedelta(days=rng.randint(1,20))
        rec['OnsetWeek']=None if not rec.get('OnsetDate') else float(epiweek(rec['OnsetDate']))
        race=['White','NativeHawaiianOtherPacificIslander','UnknownOther','Black','AmericanIndianAlaskanNative','Asian','Multiracial']
        pick=rng.choice(race+[None])          # race checkboxes are mutually exclusive via Click check code
        for r in race: rec[r]=(r==pick)
        if not rec.get('Fever'): rec['FeverTemp']=None
        if not rec.get('Died'): rec['DateOfDeath']=None
        if rec.get('DoctorVisit')!=1: rec['DoctorVisitDate']=None
        if rec.get('Hospitalized')!=1: rec['HospitalAdmissionDate']=None
def lit(v):
    if v is None: return 'NULL'
    if isinstance(v,bool): return '1' if v else '0'
    if isinstance(v,int): return str(v)
    if isinstance(v,float): return repr(v)
    if isinstance(v,datetime.datetime): return "'"+v.strftime('%Y-%m-%dT%H:%M:%S')+"'"
    return "N'"+str(v).replace("'","''")+"'"
out=['-- Synthetic records (seed %d, %d per form). Regenerate with generate_synthetic.py; do not edit by hand.'%(a.seed,a.count),'USE [%s];'%F['database'],'GO']
manifest={'seed':a.seed,'countPerForm':a.count,'forms':{}}
for form in F['forms']:
    recs=[]
    for i in range(a.count):
        rec={'GlobalRecordId':uid()}
        for p in form['pages']:
            for f in p['fields']:
                if rng.random()<0.08 and not f['required'] and f['fieldTypeId'] not in (10,11): rec[f['name']]=None
                else: rec[f['name']]=value(f,rec)
        if 'CaseID' in rec: rec['CaseID']=float(10001+i)
        apply_check_code(form,rec)
        t=datetime.datetime(2011,1,3)+datetime.timedelta(minutes=rng.randint(0,60*24*700))
        rec['_base']={'RecStatus':0 if rng.random()<0.05 else 1,'GlobalRecordId':rec['GlobalRecordId'],'FirstSaveLogonName':'synthetic','FirstSaveTime':t,
                      'LastSaveLogonName':'synthetic','LastSaveTime':t+datetime.timedelta(days=rng.randint(0,30)),'FKEY':None}
        recs.append(rec)
    bcols=['RecStatus','GlobalRecordId','FirstSaveLogonName','FirstSaveTime','LastSaveLogonName','LastSaveTime','FKEY']
    for i in range(0,len(recs),200):
        out.append('INSERT INTO [dbo].[%s] (%s) VALUES\n'%(form['name'],', '.join('[%s]'%c for c in bcols))+',\n'.join('('+', '.join(lit(r['_base'][c]) for c in bcols)+')' for r in recs[i:i+200])+';')
    for p in form['pages']:
        cols=['GlobalRecordId']+[f['name'] for f in p['fields']]
        for i in range(0,len(recs),200):
            out.append('INSERT INTO [dbo].[%s] (%s) VALUES\n'%(p['table'],', '.join('[%s]'%c for c in cols))+',\n'.join('('+', '.join(lit(r.get(c)) for c in cols)+')' for r in recs[i:i+200])+';')
    out.append('GO')
    manifest['forms'][form['name']]={'records':len(recs),'softDeleted':sum(1 for r in recs if r['_base']['RecStatus']==0)}
open(os.path.join(HERE,'sql','07_synthetic_records.sql'),'w',encoding='utf-8').write('\n'.join(out)+'\n')
json.dump(manifest,open(os.path.join(HERE,'synthetic_manifest.json'),'w'),indent=2)
print(json.dumps(manifest))
