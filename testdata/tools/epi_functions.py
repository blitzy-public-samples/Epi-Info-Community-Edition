"""Python ports of legacy Epi Info check-code functions, used to compute expected values.
Ported from Epi.Core.EnterInterpreter/Rules/Functions (Rule_Years.cs, Rule_EPIWEEK.cs)."""
import datetime
def years(d1, d2):
    """YEARS(d1, d2): new DateTime((later-earlier).Ticks).Year - 1, negated if d1 > d2."""
    if d1 is None or d2 is None: return None
    span=abs((d2-d1).days)
    y=(datetime.datetime(1,1,1)+datetime.timedelta(days=span)).year-1
    return -y if d1>d2 else y
def _mmwr_start_year(y, fdow=0):
    jan1=datetime.datetime(y,1,1); dow=(jan1.weekday()+1)%7  # .NET DayOfWeek: Sunday=0
    if dow<=fdow+3: return jan1+datetime.timedelta(days=fdow-dow)
    return jan1+datetime.timedelta(days=7-fdow-dow)
def _mmwr_start(d, fdow=0):
    s0=_mmwr_start_year(d.year-1,fdow); s=_mmwr_start_year(d.year,fdow); s1=_mmwr_start_year(d.year+1,fdow)
    if (s-d).days>0: return s0
    if (s1-d).days>0: return s
    return s1
def epiweek(d, fdow=0):
    if d is None: return None
    return (d-_mmwr_start(d,fdow)).days//7+1
