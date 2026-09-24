namespace ArdUi;

// Extra receive capacity is shared by all flows; base credits remain available to new flows.
sealed class FlowCreditBudget
{
    public const int Initial=16, Maximum=512, ExtraLimit=2048;
    readonly object sync=new();
    int used;
    public int Used{get{lock(sync)return used;}}
    public bool TryGrow()
    {lock(sync){if(used==ExtraLimit)return false;used++;return true;}}
    public void Release(int count)
    {lock(sync){if(count<0||count>used)throw new InvalidOperationException("Invalid flow credit release.");used-=count;}}
}
