"""Minimal read-only Jet4 (.mdb, Access 2000-2003) reader. Enough for tables, rows, text/memo."""
import struct, datetime, uuid
PG=4096
class Col: pass
class Jet4:
    def __init__(s, path):
        s.b=open(path,'rb').read(); s.np=len(s.b)//PG
        assert s.b[0x14]==1, 'not Jet4'
        s.types={}  # tdef page -> list of data pages
        for p in range(s.np):
            pg=s.page(p)
            if pg[0]==1 and pg[1]==1:
                s.types.setdefault(struct.unpack_from('<I',pg,4)[0],[]).append(p)
        s.catalog={}
        for r in s.rows(2):
            if r.get('Type')==1 and r.get('Name'):
                s.catalog[r['Name']]=r['Id']&0xFFFFFF
    def page(s,p): return s.b[p*PG:(p+1)*PG]
    def tdef(s,p):
        buf=bytearray(s.page(p)); nxt=struct.unpack_from('<I',buf,4)[0]
        while nxt:
            pg=s.page(nxt); buf+=pg[8:]; nxt=struct.unpack_from('<I',pg,4)[0]
        num_cols=struct.unpack_from('<H',buf,45)[0]; num_ridx=struct.unpack_from('<I',buf,51)[0]
        off=63+num_ridx*12; cols=[]
        for i in range(num_cols):
            c=Col(); e=buf[off+i*25:off+i*25+25]
            c.type=e[0]; c.num=struct.unpack_from('<H',e,5)[0]; c.var=struct.unpack_from('<H',e,7)[0]
            c.flags=e[15]; c.fixed=bool(c.flags&1); c.foff=struct.unpack_from('<H',e,21)[0]; c.size=struct.unpack_from('<H',e,23)[0]
            cols.append(c)
        off+=num_cols*25
        for c in cols:
            n=struct.unpack_from('<H',buf,off)[0]; c.name=buf[off+2:off+2+n].decode('utf-16le'); off+=2+n
        return cols
    def rowbytes(s,pg,i):
        n=struct.unpack_from('<H',pg,12)[0]
        o=struct.unpack_from('<H',pg,14+2*i)[0]
        start=o&0x1fff
        end=PG if i==0 else struct.unpack_from('<H',pg,14+2*(i-1))[0]&0x1fff
        return o, pg[start:end]
    def ptr(s,v):
        rn=v&0xff; p=v>>8; pg=s.page(p); return s.rowbytes(pg,rn)[1]
    def rows(s,tdef_pg, cols=None):
        cols=cols or s.tdef(tdef_pg)
        for p in s.types.get(tdef_pg,[]):
            pg=s.page(p); n=struct.unpack_from('<H',pg,12)[0]
            for i in range(n):
                o,row=s.rowbytes(pg,i)
                if o&0x8000: continue  # deleted
                if o&0x4000: row=s.ptr(struct.unpack_from('<I',row,0)[0])
                if len(row)<3: continue
                yield s.crack(row,cols)
    def crack(s,row,cols):
        ncols=struct.unpack_from('<H',row,0)[0]; bm=(ncols+7)//8; end=len(row)-1
        nm=row[end-bm+1:]
        nvar=struct.unpack_from('<H',row,end-bm-1)[0]
        voff=[struct.unpack_from('<H',row,end-bm-3-2*i)[0] for i in range(nvar+1)]
        out={}
        for c in cols:
            present = c.num//8 < len(nm) and (nm[c.num//8]>>(c.num%8))&1
            if c.type==1: out[c.name]=bool(present); continue
            if not present or c.num>=ncols: out[c.name]=None; continue
            if c.fixed:
                d=row[2+c.foff:2+c.foff+c.size]
            else:
                if c.var>=nvar: out[c.name]=None; continue
                d=row[voff[c.var]:voff[c.var+1]]
            out[c.name]=s.val(c,d)
        return out
    def text(s,d):
        if d[:2]==b'\xff\xfe':
            d=d[2:]; res=[]; comp=True; i=0
            while i<len(d):
                if d[i]==0: comp=not comp; i+=1; continue
                if comp: res.append(chr(d[i])); i+=1
                else: res.append(d[i:i+2].decode('utf-16le','replace')); i+=2
            return ''.join(res)
        return d.decode('utf-16le','replace')
    def memo(s,d):
        if len(d)<12: return None
        ln=struct.unpack_from('<I',d,0)[0]; flags=ln&0xC0000000; ln&=0x3FFFFFFF
        if flags&0x80000000: raw=d[12:12+ln]
        elif flags&0x40000000: raw=s.ptr(struct.unpack_from('<I',d,4)[0])[:ln]
        else:
            raw=b''; v=struct.unpack_from('<I',d,4)[0]
            while v and len(raw)<ln:
                r=s.ptr(v); v=struct.unpack_from('<I',r,0)[0]; raw+=r[4:]
            raw=raw[:ln]
        return raw
    def val(s,c,d):
        t=c.type
        if t==2: return d[0]
        if t==3: return struct.unpack('<h',d[:2])[0]
        if t==4: return struct.unpack('<i',d[:4])[0]
        if t==5: return struct.unpack('<q',d[:8])[0]/10000
        if t==6: return float('%.7g'%struct.unpack('<f',d[:4])[0])
        if t==7: return struct.unpack('<d',d[:8])[0]
        if t==8:
            v=struct.unpack('<d',d[:8])[0]
            try: return datetime.datetime(1899,12,30)+datetime.timedelta(days=v)
            except Exception: return None
        if t==10: return s.text(d)
        if t==12:
            r=s.memo(d); return None if r is None else s.text(r)
        if t==15: return str(uuid.UUID(bytes_le=bytes(d[:16])))
        if t==16:
            prec,scale,neg=d[0],d[1],d[2]
            n=int.from_bytes(bytes(d[3:7])[::-1]+bytes(d[7:11])[::-1]+bytes(d[11:15])[::-1]+bytes(d[15:19])[::-1],'big') if len(d)>=19 else 0
            v=n/(10**scale); return -v if neg else v
        if t==11:
            r=s.memo(d); return None if r is None else bytes(r)
        if t==9: return bytes(d)
        return bytes(d)
    def table(s,name):
        p=s.catalog[name]; cols=s.tdef(p); return cols, list(s.rows(p,cols))
