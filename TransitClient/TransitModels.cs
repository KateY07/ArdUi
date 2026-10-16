namespace ArdUi;

sealed record PathQuality(int Samples,int Received,double MedianMs,double P90Ms,double JitterMs,double Loss,double? Mbps)
{
    public double Score=>P90Ms+2*JitterMs+1000*Loss;
    public bool Eligible=>Samples>=8&&Loss<=.125;
    public bool DegradedFrom(PathQuality baseline)=>Samples>=8&&baseline.Samples>=8&&
        (Loss>.25&&Loss>baseline.Loss||Score>baseline.Score*1.3+5);
    public bool SlowerThan(PathQuality baseline)=>Received>=8&&baseline.Received>=8&&
        P90Ms+2*JitterMs>(baseline.P90Ms+2*baseline.JitterMs)*1.3+5;
    public static PathQuality From(double?[] samples,double? mbps)
    {
        var values=samples.Where(x=>x.HasValue).Select(x=>x!.Value).Order().ToArray();
        if(values.Length==0)return new(samples.Length,0,1e6,1e6,1e6,1,mbps);
        var median=values[values.Length/2];var p90=values[(int)Math.Ceiling(values.Length*.9)-1];
        return new(samples.Length,values.Length,median,p90,p90-median,1-values.Length/(double)samples.Length,mbps);
    }
    public bool BetterThan(PathQuality baseline)
    {
        if(!Eligible||Loss>Math.Max(.125,baseline.Loss))return false;
        if(baseline.Samples<8)return false;
        // A measured lossy baseline may never finish its bandwidth transfer.
        // Its missing bandwidth must not veto a healthy, lower-cost route.
        if(!baseline.Eligible&&Score<baseline.Score)return true;
        if(Mbps is not >0||baseline.Mbps is not >0||Mbps<baseline.Mbps*.7)return false;
        return baseline.Score-Score>=Math.Max(5,baseline.Score*.15)||
            (baseline.Mbps is >0&&Mbps>=baseline.Mbps*1.5&&P90Ms<=baseline.P90Ms+5&&Loss<=baseline.Loss);
    }
}
sealed record TransitCandidate(string Endpoint,int Capacity,int Active,int Mbps,long Seen);
sealed record TransitCandidates(TransitCandidate[] Candidates);
sealed record TransitTicket(string Route,string A,string B,string C,string ASession,string BSession,long Expires,int Mbps);
sealed record TransitOffer(string Route,string ARelaySession,string BRelaySession,string AToken,string BToken);
sealed record TransitReply(string Status,SignedEnvelope Ticket,SignedEnvelope? Offer);
sealed record TransitActivation(SignedEnvelope Ticket,SignedEnvelope Offer);
