using Godot;
using System;
using System.Linq;

public partial class RPSButton : Button
{
    [Export]
    private RockPaperScissors.Choice choiceType;
    
    [Export]
    private RockPaperScissors rpsGame;
    
    public override void _Ready()
    {
        Pressed += OnButtonPressed;
        
        // If rpsGame wasn't assigned in the editor, try to find it
        if (rpsGame == null)
        {
            try
            {
                // Try to get the parent RPS game
                rpsGame = GetParent()?.GetParent()?.GetParent() as RockPaperScissors;
                
                if (rpsGame == null)
                {
                    // Try to find it in the scene
                    rpsGame = GetTree().Root.FindChild("RockPaperScissors", true, false) as RockPaperScissors;
                }
                
                if (rpsGame != null)
                {
                    GD.Print($"RPSButton: Found RPS game reference for {Name}");
                }
                else
                {
                    GD.PrintErr($"RPSButton: Could not find RPS game reference for {Name}");
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"RPSButton: Error finding RPS game: {ex.Message}");
            }
        }
    }
    
    private void OnButtonPressed()
    {
        try
        {
            if (rpsGame == null)
            {
                GD.PrintErr($"RPSButton: No RPS game reference available for {Name}");
                return;
            }
            
            rpsGame.SetPlayer1Choice(choiceType);
            
            // Disable all buttons after selection
            if (GetParent() != null)
            {
                try
                {
                    GetParent().GetChildren().Cast<Node>()
                        .OfType<Button>()
                        .ToList()
                        .ForEach(button => button.Disabled = true);
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"RPSButton: Error disabling buttons: {ex.Message}");
                    // Fall back to just disabling this button
                    Disabled = true;
                }
            }
            else
            {
                // Just disable this button if parent is null
                Disabled = true;
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"RPSButton: Error in OnButtonPressed: {ex.Message}");
        }
    }
}