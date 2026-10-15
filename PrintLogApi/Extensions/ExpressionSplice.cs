using System.Linq.Expressions;

namespace PrintLogApi.Extensions;

/// <summary>
/// Builds one expression tree out of several, so a figure EF must translate (a spool's
/// remaining weight, say) is written once and reused inside other projections. A lambda cannot
/// call another expression and still translate to SQL; replacing a placeholder parameter with the
/// other expression's body produces the same tree the compiler would have built had it been
/// written inline.
/// </summary>
public static class ExpressionSplice
{
    /// <summary>
    /// <paramref name="shape"/> with its second parameter replaced by <paramref name="figure"/>
    /// applied to its first: <c>(x, v) =&gt; new { x.Id, V = v }</c> spliced with
    /// <c>x =&gt; x.A + x.B</c> becomes <c>x =&gt; new { x.Id, V = x.A + x.B }</c>.
    /// </summary>
    public static Expression<Func<TSource, TResult>> With<TSource, TValue, TResult>(
        Expression<Func<TSource, TValue, TResult>> shape,
        Expression<Func<TSource, TValue>> figure)
    {
        var source = shape.Parameters[0];
        var value = Replace(figure.Body, figure.Parameters[0], source);
        return Expression.Lambda<Func<TSource, TResult>>(Replace(shape.Body, shape.Parameters[1], value), source);
    }

    /// <summary>
    /// <see cref="With{TSource, TValue, TResult}"/> for two figures, filling the second and third
    /// parameters of <paramref name="shape"/>.
    /// </summary>
    public static Expression<Func<TSource, TResult>> With<TSource, T1, T2, TResult>(
        Expression<Func<TSource, T1, T2, TResult>> shape,
        Expression<Func<TSource, T1>> first,
        Expression<Func<TSource, T2>> second)
    {
        var source = shape.Parameters[0];
        var body = Replace(shape.Body, shape.Parameters[1], Replace(first.Body, first.Parameters[0], source));
        body = Replace(body, shape.Parameters[2], Replace(second.Body, second.Parameters[0], source));
        return Expression.Lambda<Func<TSource, TResult>>(body, source);
    }

    /// <summary>
    /// <paramref name="shape"/> with its second parameter replaced by <paramref name="argument"/>
    /// itself, for a placeholder that stands for a lambda: <c>(f, row) =&gt; f.Rows.Sum(row)</c>
    /// with <c>r =&gt; r.Mg</c> becomes <c>f =&gt; f.Rows.Sum(r =&gt; r.Mg)</c>.
    /// </summary>
    public static Expression<Func<TSource, TResult>> Inline<TSource, TArgument, TResult>(
        Expression<Func<TSource, TArgument, TResult>> shape,
        Expression<TArgument> argument)
        where TArgument : Delegate =>
        Expression.Lambda<Func<TSource, TResult>>(Replace(shape.Body, shape.Parameters[1], argument), shape.Parameters[0]);

    /// <summary>Replaces every occurrence of <paramref name="from"/> in <paramref name="body"/>.</summary>
    public static Expression Replace(Expression body, Expression from, Expression to) =>
        new Replacer(from, to).Visit(body)
        ?? throw new InvalidOperationException("Rewriting an expression produced nothing.");

    private sealed class Replacer(Expression from, Expression to) : ExpressionVisitor
    {
        public override Expression? Visit(Expression? node) => node == from ? to : base.Visit(node);
    }
}
