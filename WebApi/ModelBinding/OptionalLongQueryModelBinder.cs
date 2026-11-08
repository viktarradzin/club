using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;

namespace WebApi.ModelBinding;

public sealed class OptionalLongQueryModelBinder : IModelBinder
{
    private readonly SimpleTypeModelBinder _innerBinder;

    public OptionalLongQueryModelBinder(ILoggerFactory loggerFactory)
    {
        _innerBinder = new SimpleTypeModelBinder(typeof(long?), loggerFactory);
    }

    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var value = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);

        if (value != ValueProviderResult.None && string.IsNullOrWhiteSpace(value.FirstValue))
        {
            bindingContext.ModelState.SetModelValue(bindingContext.ModelName, value);
            bindingContext.ModelState.TryAddModelError(
                bindingContext.ModelName,
                bindingContext.ModelMetadata.ModelBindingMessageProvider.AttemptedValueIsInvalidAccessor(
                    value.FirstValue ?? string.Empty, bindingContext.ModelName));
            bindingContext.Result = ModelBindingResult.Failed();

            return Task.CompletedTask;
        }

        return _innerBinder.BindModelAsync(bindingContext);
    }
}
