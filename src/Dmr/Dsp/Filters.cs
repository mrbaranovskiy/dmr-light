namespace Dmr.Dsp;

public static class Filters
{
    public static double[] RootRaisedCosine(int samplesPerSymbol=10,int span=10,double alpha=.2)
    {
        int n=samplesPerSymbol*span+1; var h=new double[n];
        for(int i=0;i<n;i++)
        {
            double t=(i-(n-1)/2.0)/samplesPerSymbol;
            if(Math.Abs(t)<1e-9) h[i]=1+alpha*(4/Math.PI-1);
            else if(Math.Abs(Math.Abs(t)-1/(4*alpha))<1e-9)
                h[i]=alpha/Math.Sqrt(2)*((1+2/Math.PI)*Math.Sin(Math.PI/(4*alpha))+(1-2/Math.PI)*Math.Cos(Math.PI/(4*alpha)));
            else h[i]=(Math.Sin(Math.PI*t*(1-alpha))+4*alpha*t*Math.Cos(Math.PI*t*(1+alpha)))/(Math.PI*t*(1-16*alpha*alpha*t*t));
        }
        double sum=h.Sum(); for(int i=0;i<n;i++) h[i]/=sum;
        return h;
    }
}

internal sealed class Fir(double[] coefficients)
{
    private readonly double[] memory=new double[coefficients.Length];
    private int head;
    public double Push(double value)
    {
        memory[head]=value; double result=0; int j=head;
        for(int i=0;i<coefficients.Length;i++) { result+=coefficients[i]*memory[j]; if(--j<0) j=memory.Length-1; }
        if(++head==memory.Length) head=0;
        return result;
    }
}
