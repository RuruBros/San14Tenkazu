using System.Globalization;
using San14Tenkazu.EnvironmentRules;
using W=System.Windows;
using WC=System.Windows.Controls;
using WM=System.Windows.Media;
namespace San14Tenkazu;

internal sealed class EnvironmentSettingsWindow:W.Window
{
    internal readonly Dictionary<EnvironmentEffects,WC.CheckBox> Choices=[];
    internal readonly Dictionary<string,WC.TextBox> Numbers=[];
    internal readonly PlateButton Apply=new(L.Environment.Apply,primary:true),DisableAll=new(L.Environment.DisableAll);
    internal readonly WC.TextBlock Status=Text(""),SeasonStatus=Text("");
    private readonly WC.TextBlock inputError=Text("");
    private readonly Dictionary<string,(string Label,int Max,int Scale)> limits=[];
    private readonly WC.ListBox history=new(){Height=130,Background=CardSkin.Parchment,BorderBrush=CardSkin.Gold};
    private readonly WC.StackPanel visual=new();
    private readonly W.FrameworkElement seasonChoices;
    private bool canApply,validInputs;
    internal EnvironmentSettingsWindow(W.FrameworkElement seasonChoices,EnvironmentSettings settings,IEnumerable<string> events)
    {
        this.seasonChoices=seasonChoices;Title=L.Environment.Menu;Width=700;Height=900;MinWidth=570;MinHeight=550;
        ShowInTaskbar=false;WindowStartupLocation=W.WindowStartupLocation.CenterOwner;Background=CardSkin.Parchment;
        FontFamily=CardSkin.Font;FontSize=CardSkin.FontSize;Foreground=CardSkin.Ink;UseLayoutRounding=true;
        WM.TextOptions.SetTextFormattingMode(this,WM.TextFormattingMode.Display);
        var content=new WC.StackPanel{Margin=new W.Thickness(18)};var v=settings.Values;
        content.Children.Add(Text(L.Environment.Independent));
        content.Children.Add(Heading(L.Environment.Visual));visual.Children.Add(seasonChoices);content.Children.Add(visual);
        content.Children.Add(SeasonStatus);content.Children.Add(Text(L.Environment.VisualHint));
        content.Children.Add(Heading(L.Environment.WeatherVisual));
        Choice(content,EnvironmentEffects.RainVisual,L.Environment.RainVisual);Choice(content,EnvironmentEffects.SnowVisual,L.Environment.SnowVisual);
        content.Children.Add(Text(L.Environment.WeatherVisualHint));
        content.Children.Add(Text(L.Environment.OverlayHint));
        content.Children.Add(Heading(L.Environment.Weather));
        var rain=Choice(content,EnvironmentEffects.RainMovement,L.Environment.Rain);
        Number(rain,nameof(v.RainReduction),L.Environment.RainRate,v.RainReduction,99,true);
        Number(rain,nameof(v.HeavyRainReduction),L.Environment.HeavyRainRate,v.HeavyRainReduction,99,true);
        var snow=Choice(content,EnvironmentEffects.SnowMovement,L.Environment.Snow);
        Number(snow,nameof(v.SnowReduction),L.Environment.SnowRate,v.SnowReduction,99,true);
        Number(snow,nameof(v.DeepSnowReduction),L.Environment.DeepSnowRate,v.DeepSnowReduction,99,true);
        Number(Choice(content,EnvironmentEffects.WeatherMorale,L.Environment.Morale),nameof(v.WeatherMoraleLoss),L.Environment.MoraleRate,v.WeatherMoraleLoss,130,false);
        content.Children.Add(Heading(L.Environment.Disasters));
        var flood=Choice(content,EnvironmentEffects.Flood,L.Environment.Flood);
        Number(flood,nameof(v.FloodChance),L.Environment.ChanceRate,v.FloodChance,1000,true,10);
        Number(flood,nameof(v.FloodTroopsPercent),L.Environment.TroopsRate,v.FloodTroopsPercent,99,true);
        Number(flood,nameof(v.FloodMoraleLoss),L.Environment.DisasterMoraleRate,v.FloodMoraleLoss,130,false);
        Number(flood,nameof(v.FloodFoodPercent),L.Environment.FoodRate,v.FloodFoodPercent,100,true);
        var fire=Choice(content,EnvironmentEffects.Wildfire,L.Environment.Wildfire);
        Number(fire,nameof(v.WildfireChance),L.Environment.ChanceRate,v.WildfireChance,1000,true,10);
        Number(fire,nameof(v.WildfireTroopsPercent),L.Environment.TroopsRate,v.WildfireTroopsPercent,99,true);
        Number(fire,nameof(v.WildfireMoraleLoss),L.Environment.DisasterMoraleRate,v.WildfireMoraleLoss,130,false);
        Number(fire,nameof(v.WildfireFoodPercent),L.Environment.FoodRate,v.WildfireFoodPercent,100,true);
        var drought=Choice(content,EnvironmentEffects.Drought,L.Environment.Drought);
        Number(drought,nameof(v.DroughtChance),L.Environment.ChanceRate,v.DroughtChance,1000,true,10);
        Number(drought,nameof(v.DroughtFoodPercent),L.Environment.FoodRate,v.DroughtFoodPercent,100,true);
        var blizzard=Choice(content,EnvironmentEffects.Blizzard,L.Environment.Blizzard);
        Number(blizzard,nameof(v.BlizzardChance),L.Environment.ChanceRate,v.BlizzardChance,1000,true,10);
        Number(blizzard,nameof(v.BlizzardTroopsPercent),L.Environment.TroopsRate,v.BlizzardTroopsPercent,99,true);
        Number(blizzard,nameof(v.BlizzardMoraleLoss),L.Environment.DisasterMoraleRate,v.BlizzardMoraleLoss,130,false);
        Number(blizzard,nameof(v.BlizzardFoodPercent),L.Environment.FoodRate,v.BlizzardFoodPercent,100,true);
        var heatwave=Choice(content,EnvironmentEffects.Heatwave,L.Environment.Heatwave);
        Number(heatwave,nameof(v.HeatwaveChance),L.Environment.ChanceRate,v.HeatwaveChance,1000,true,10);
        Number(heatwave,nameof(v.HeatwaveTroopsPercent),L.Environment.TroopsRate,v.HeatwaveTroopsPercent,99,true);
        Number(heatwave,nameof(v.HeatwaveMoraleLoss),L.Environment.DisasterMoraleRate,v.HeatwaveMoraleLoss,130,false);
        Number(heatwave,nameof(v.HeatwaveFoodPercent),L.Environment.FoodRate,v.HeatwaveFoodPercent,100,true);
        foreach(var pair in Choices)pair.Value.IsChecked=(settings.Effects&pair.Key)!=0;
        content.Children.Add(Heading(L.Environment.Rules));content.Children.Add(Text(L.Environment.Summary));content.Children.Add(Text(L.Environment.Losses));
        inputError.Foreground=CardSkin.Poor;content.Children.Add(inputError);content.Children.Add(Status);
        var buttons=new WC.WrapPanel{Margin=new W.Thickness(0,10,0,10)};
        Apply.Margin=new W.Thickness(0,0,10,0);DisableAll.Margin=new W.Thickness(0,0,10,0);
        buttons.Children.Add(Apply);buttons.Children.Add(DisableAll);
        var close=new PlateButton(L.Environment.Close);close.Click+=Close;buttons.Children.Add(close);content.Children.Add(buttons);
        content.Children.Add(Heading(L.Environment.History));content.Children.Add(history);
        foreach(var box in Numbers.Values)box.TextChanged+=(_,_)=>ValidateInputs();SetHistory(events);ValidateInputs();
        Content=new WC.ScrollViewer{Content=content,Background=CardSkin.Parchment,VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=WC.ScrollBarVisibility.Disabled};
        Closed+=(_,_)=>visual.Children.Remove(seasonChoices);
        PreviewKeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Escape){Close();e.Handled=true;}};
    }
    private static WC.TextBlock Text(string text)=>new(){Text=text,TextWrapping=W.TextWrapping.Wrap,Foreground=CardSkin.Ink,Margin=new W.Thickness(0,3,8,3)};
    private static WC.TextBlock Heading(string text){var result=Text(text);result.FontWeight=W.FontWeights.Bold;result.FontSize=16;result.Margin=new W.Thickness(0,12,0,4);return result;}
    private WC.WrapPanel Choice(WC.Panel parent,EnvironmentEffects effect,string label)
    {
        var check=new WC.CheckBox{Content=new WC.TextBlock{Text=label,TextWrapping=W.TextWrapping.Wrap},Margin=new W.Thickness(0,4,0,2),Foreground=CardSkin.Ink,
            HorizontalContentAlignment=W.HorizontalAlignment.Stretch};Choices.Add(effect,check);parent.Children.Add(check);
        var row=new WC.WrapPanel{Margin=new W.Thickness(22,0,0,6)};parent.Children.Add(row);return row;
    }
    private void Number(WC.Panel parent,string key,string label,int value,int max,bool percent,int scale=1)
    {
        var row=new WC.StackPanel{Orientation=WC.Orientation.Horizontal,Margin=new W.Thickness(0,2,14,2)};
        var box=CardSkin.NumberBox(58);box.Text=((decimal)value/scale).ToString("0.#",CultureInfo.CurrentCulture);
        box.ToolTip=L.Environment.NumberRange(label,(decimal)max/scale,scale==10?L.Environment.DecimalStep:L.Environment.IntegerStep);
        System.Windows.Automation.AutomationProperties.SetName(box,label);Numbers.Add(key,box);limits.Add(key,(label,max,scale));
        row.Children.Add(Text(label));row.Children.Add(box);if(percent)row.Children.Add(Text(L.Environment.Percent));parent.Children.Add(row);
    }
    private bool TryValues(out EnvironmentValues values,out string error)
    {
        var parsed=new Dictionary<string,int>();error="";values=default;
        foreach(var pair in Numbers)
        {
            var rule=limits[pair.Key];
            if(!decimal.TryParse(pair.Value.Text,NumberStyles.AllowDecimalPoint|NumberStyles.AllowLeadingWhite|NumberStyles.AllowTrailingWhite,CultureInfo.CurrentCulture,out var number)
                ||number<0||number>rule.Max/(decimal)rule.Scale||number*rule.Scale!=decimal.Truncate(number*rule.Scale))
            {error=L.Environment.NumberRange(rule.Label,(decimal)rule.Max/rule.Scale,rule.Scale==10?L.Environment.DecimalStep:L.Environment.IntegerStep);return false;}
            parsed.Add(pair.Key,(int)(number*rule.Scale));
        }
        int Read(string name)=>parsed[name];
        values=new(Read(nameof(EnvironmentValues.RainReduction)),Read(nameof(EnvironmentValues.HeavyRainReduction)),Read(nameof(EnvironmentValues.SnowReduction)),Read(nameof(EnvironmentValues.DeepSnowReduction)),Read(nameof(EnvironmentValues.WeatherMoraleLoss)),
            Read(nameof(EnvironmentValues.FloodChance)),Read(nameof(EnvironmentValues.FloodTroopsPercent)),Read(nameof(EnvironmentValues.FloodMoraleLoss)),Read(nameof(EnvironmentValues.FloodFoodPercent)),
            Read(nameof(EnvironmentValues.WildfireChance)),Read(nameof(EnvironmentValues.WildfireTroopsPercent)),Read(nameof(EnvironmentValues.WildfireMoraleLoss)),Read(nameof(EnvironmentValues.WildfireFoodPercent)),Read(nameof(EnvironmentValues.DroughtChance)),Read(nameof(EnvironmentValues.DroughtFoodPercent)),
            Read(nameof(EnvironmentValues.BlizzardChance)),Read(nameof(EnvironmentValues.BlizzardTroopsPercent)),Read(nameof(EnvironmentValues.BlizzardMoraleLoss)),Read(nameof(EnvironmentValues.BlizzardFoodPercent)),
            Read(nameof(EnvironmentValues.HeatwaveChance)),Read(nameof(EnvironmentValues.HeatwaveTroopsPercent)),Read(nameof(EnvironmentValues.HeatwaveMoraleLoss)),Read(nameof(EnvironmentValues.HeatwaveFoodPercent)));return true;
    }
    private void ValidateInputs(){validInputs=TryValues(out _,out string error);inputError.Text=error;Apply.IsEnabled=canApply&&validInputs;}
    internal EnvironmentSettings Selection(EnvironmentSettings current)
    {
        var flags=Choices.Aggregate(EnvironmentEffects.None,(value,p)=>p.Value.IsChecked==true?value|p.Key:value);
        if(!TryValues(out var values,out _)&&flags!=EnvironmentEffects.None)throw new InvalidDataException(L.Environment.InvalidSettings);
        return current with{Effects=flags,Values=validInputs?values:current.Values};
    }
    internal void ClearSelection(){foreach(var check in Choices.Values)check.IsChecked=false;}
    internal void SetHistory(IEnumerable<string> rows)
    {
        history.Items.Clear();foreach(string row in rows)history.Items.Add(new WC.TextBlock{Text=row,TextWrapping=W.TextWrapping.Wrap,MaxWidth=610,Foreground=CardSkin.Ink});
        if(history.Items.Count==0)history.Items.Add(L.Environment.NoEvents);
    }
    internal void SetAvailable(bool apply,bool stop){canApply=apply;Apply.IsEnabled=apply&&validInputs;DisableAll.IsEnabled=stop;}
}
