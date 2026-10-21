using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace TraceSoul2.Plugins;

// Both delivery paths consume the same scores. This never scores again, chooses
// default artwork or sends; invalid files are skipped before any side effect.
public static class ExpressionSelection
{
    public static (string id,ExpressionImage image)? ReadHighest(IExpressionLibrary library,ExpressionMatch match,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(library==null||match?.status!="matched")return null;
        var candidates=match.candidates.Where(c=>c.eligible).OrderByDescending(c=>c.score).ThenBy(c=>c.id,StringComparer.Ordinal).Select(c=>c.id);
        // Older scorers may only provide a selected asset.
        if(!candidates.Any()&&!string.IsNullOrEmpty(match.selectedId))candidates=new[]{match.selectedId};
        foreach(var id in candidates)
        {
            token.ThrowIfCancellationRequested();
            try {return (id,library.ReadImage(id));}
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException){}
        }
        token.ThrowIfCancellationRequested();
        return null;
    }
}
