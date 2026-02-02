using System;
using System.Reactive.Linq;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using HyPrism.Services.Core;

namespace HyPrism.UI.Converters;

/// <summary>
/// Reactive markup extension for localization in AXAML.
/// Usage: Text="{loc:Localize settings.title}"
/// Automatically updates when language changes via ReactiveUI WhenAnyValue.
/// </summary>
public class LocalizeExtension : MarkupExtension
{
    public string Key { get; set; }
    
    public LocalizeExtension()
    {
        Key = string.Empty;
    }
    
    public LocalizeExtension(string key)
    {
        Key = key;
    }
    
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrEmpty(Key))
            return new BindingNotification(new InvalidOperationException("LocalizeExtension: Key is required"), BindingErrorType.Error);
        
        // Use GetObservable which creates a reactive stream via WhenAnyValue
        // This is the ReactiveUI way - it observes CurrentLanguage changes
        var observable = LocalizationService.Instance.GetObservable(Key);
        
        // Convert to Avalonia binding
        return new ObservableBinding(observable);
    }
}

/// <summary>
/// Wrapper to convert IObservable to Avalonia binding
/// </summary>
internal class ObservableBinding
{
    private readonly IObservable<string> _observable;
    
    public ObservableBinding(IObservable<string> observable)
    {
        _observable = observable;
    }
    
    public static implicit operator Avalonia.Data.Binding(ObservableBinding wrapper)
    {
        // Create a subject to push values through
        var subject = new System.Reactive.Subjects.ReplaySubject<string>(1);
        wrapper._observable.Subscribe(subject);
        
        // Return as observable binding
        return new Avalonia.Data.Binding
        {
            Source = subject,
            Path = "."
        };
    }
}
