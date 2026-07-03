using System.Collections.Generic;

namespace ZeroFall.DataTable.ViewModels;

public interface IRowFilter<in TRow>
{
    bool Match(TRow row);
}

public interface IFilterPipeline<TRow>
{
    IEnumerable<TRow> Apply(IEnumerable<TRow> source);
}
