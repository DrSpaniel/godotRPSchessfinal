using Godot;
using System;
using static Board;

public partial class RockPaperScissors : Control
{
    public enum Choice
    {
        None,
        Rock,
        Paper,
        Scissors
    }

    public enum Result
    {
        None,
        Player1Win,
        Player2Win,
        Draw
    }

    [Signal]
    public delegate void GameFinishedEventHandler(Result result);

    [Export]
    private TextureRect player1ChoiceDisplay;
    
    [Export]
    private TextureRect player2ChoiceDisplay;
    
    [Export]
    private Label resultLabel;
    
    [Export]
    private Label promptLabel;
    
    [Export]
    private Texture2D rockTexture;
    
    [Export]
    private Texture2D paperTexture;
    
    [Export]
    private Texture2D scissorsTexture;

    // Capture information
    private Move captureMove;
    private Piece.Color playerColor;
    
    private Choice player1Choice = Choice.None;
    private Choice player2Choice = Choice.None;
    
    // This prevents the timer callback issues when the scene is freed
    private bool isBeingDestroyed = false;

    public override void _Ready()
    {
        GD.Print("RockPaperScissors scene is ready");
        
        // Load textures if they weren't set in the editor
        LoadTexturesIfNeeded();
        
        // Check if our UI nodes are properly connected
        if (player1ChoiceDisplay == null || player2ChoiceDisplay == null || 
            resultLabel == null || promptLabel == null)
        {
            GD.PrintErr("RPS UI elements not properly connected - some nodes are null");
            // Try to find them by path if they weren't connected in the editor
            TryFindUIComponents();
        }
        
        // Check if textures are loaded
        if (rockTexture == null || paperTexture == null || scissorsTexture == null)
        {
            GD.PrintErr("RPS textures not properly loaded - some textures are null");
        }
        
        // Handle display safely with null checks
        SafelySetupDisplay();
        
        // Start a safety timer in case the AutoPlayTimer doesn't work
        GetTree().CreateTimer(15.0).Timeout += () => {
            GD.Print("Safety timeout - auto-selecting Rock after 15 seconds");
            if (player1Choice == Choice.None && !isBeingDestroyed)
            {
                SetPlayer1Choice(Choice.Rock);
            }
        };
    }
    
    private void LoadTexturesIfNeeded()
    {
        try
        {
            // Load textures if they weren't set in the editor
            if (rockTexture == null)
            {
                rockTexture = GD.Load<Texture2D>("res://Assets/Sprites/classicpieces.png");
                GD.Print("Loaded fallback texture for rock");
            }
            
            if (paperTexture == null)
            {
                paperTexture = GD.Load<Texture2D>("res://Assets/Sprites/classicpieces.png");
                GD.Print("Loaded fallback texture for paper");
            }
            
            if (scissorsTexture == null)
            {
                scissorsTexture = GD.Load<Texture2D>("res://Assets/Sprites/classicpieces.png");
                GD.Print("Loaded fallback texture for scissors");
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Error loading textures: {ex.Message}");
        }
    }
    
    private void TryFindUIComponents()
    {
        try
        {
            // Try to find components if they weren't assigned in the editor
            if (player1ChoiceDisplay == null)
            {
                player1ChoiceDisplay = GetNodeOrNull<TextureRect>("CenterContainer/Panel/Displays/Player1Container/Player1ChoiceDisplay");
                GD.Print("Attempting to find player1ChoiceDisplay by path: " + (player1ChoiceDisplay != null ? "found" : "not found"));
            }
            
            if (player2ChoiceDisplay == null)
            {
                player2ChoiceDisplay = GetNodeOrNull<TextureRect>("CenterContainer/Panel/Displays/Player2Container/Player2ChoiceDisplay");
                GD.Print("Attempting to find player2ChoiceDisplay by path: " + (player2ChoiceDisplay != null ? "found" : "not found"));
            }
            
            if (resultLabel == null)
            {
                resultLabel = GetNodeOrNull<Label>("CenterContainer/Panel/ResultLabel");
                GD.Print("Attempting to find resultLabel by path: " + (resultLabel != null ? "found" : "not found"));
            }
            
            if (promptLabel == null)
            {
                promptLabel = GetNodeOrNull<Label>("CenterContainer/Panel/PromptLabel");
                GD.Print("Attempting to find promptLabel by path: " + (promptLabel != null ? "found" : "not found"));
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Error finding UI components: {ex.Message}");
        }
    }
    
    private void SafelySetupDisplay()
    {
        try
        {
            // Display which piece is attacking which piece - with safety checks
            bool validMove = captureMove.pieceSource.type != Piece.Type.None && 
                             captureMove.pieceTarget.type != Piece.Type.None;
            
            if (validMove && promptLabel != null)
            {
                string message = $"{captureMove.pieceSource.type} attacks {captureMove.pieceTarget.type}! Choose your weapon!";
                GD.Print("Setting prompt: " + message);
                promptLabel.Text = message;
            }
            else
            {
                GD.PrintErr(validMove ? "Cannot set prompt - promptLabel is null" : 
                                     "Invalid capture move - source or target piece is None");
                
                // Set a default message if we can
                if (promptLabel != null)
                {
                    promptLabel.Text = "Capture challenge! Choose your weapon!";
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Error setting up display: {ex.Message}");
        }
    }
    
    public override void _Process(double delta)
    {
        // Add basic heartbeat to check if scene is responsive
        // Only log occasional updates to avoid spamming the console
        if (Time.GetTicksMsec() % 5000 < 20)  // Log approximately every 5 seconds
        {
            GD.Print($"RPS scene heartbeat - Player1: {player1Choice}, Player2: {player2Choice}");
        }
    }
    
    public override void _ExitTree()
    {
        // Mark as being destroyed to prevent timer callbacks from running
        GD.Print("RPS scene is being destroyed");
        isBeingDestroyed = true;
    }

    public void SetCaptureMove(Move move, Piece.Color color)
    {
        GD.Print($"SetCaptureMove called: {move.pieceSource.type} from {move.squareSourceIndex} captures {move.pieceTarget.type} at {move.squareTargetIndex}");
        captureMove = move;
        playerColor = color;
    }

    public void SetPlayer1Choice(Choice choice)
    {
        GD.Print($"Player1 chose: {choice}");
        
        // Prevent input during transition
        if (player1Choice != Choice.None || isBeingDestroyed)
        {
            GD.Print("Ignoring repeated choice or choice after destruction");
            return;
        }
            
        player1Choice = choice;
        UpdatePlayerChoiceDisplay();
        
        // When player makes a choice, computer makes a random choice
        MakeComputerChoice();
    }

    private void MakeComputerChoice()
    {
        try
        {
            // Generate random choice for computer (1-3)
            Random random = new Random();
            player2Choice = (Choice)random.Next(1, 4);
            
            GD.Print($"Computer chose: {player2Choice}");
            
            UpdatePlayerChoiceDisplay();
            
            // Determine the result after a short delay
            var timer = GetTree().CreateTimer(1.0);
            timer.Timeout += DetermineResult;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Error in MakeComputerChoice: {ex.Message}");
            // Force a default result if we can't make a proper choice
            var timer = GetTree().CreateTimer(1.0);
            timer.Timeout += () => EmitResultSignal(Result.Draw);
        }
    }

    private void UpdatePlayerChoiceDisplay()
    {
        try 
        {
            GD.Print("Updating choice displays");
            
            // Update player 1 display with null checks
            if (player1Choice != Choice.None && player1ChoiceDisplay != null && 
                rockTexture != null && paperTexture != null && scissorsTexture != null)
            {
                player1ChoiceDisplay.Texture = player1Choice switch
                {
                    Choice.Rock => rockTexture,
                    Choice.Paper => paperTexture,
                    Choice.Scissors => scissorsTexture,
                    _ => null
                };
            }
            
            // Update player 2 display with null checks
            if (player2Choice != Choice.None && player2ChoiceDisplay != null && 
                rockTexture != null && paperTexture != null && scissorsTexture != null)
            {
                player2ChoiceDisplay.Texture = player2Choice switch
                {
                    Choice.Rock => rockTexture,
                    Choice.Paper => paperTexture,
                    Choice.Scissors => scissorsTexture,
                    _ => null
                };
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr("Error updating RPS display: " + ex.Message);
        }
    }

    private void DetermineResult()
    {
        // Skip if being destroyed to prevent crashes
        if (isBeingDestroyed)
        {
            GD.Print("Skipping result determination because scene is being destroyed");
            return;
        }
        
        try
        {    
            Result result = Result.None;
            
            if (player1Choice == player2Choice)
            {
                result = Result.Draw;
                if (resultLabel != null)
                    resultLabel.Text = "It's a Draw!";
                GD.Print("RPS result: Draw");
            }
            else if ((player1Choice == Choice.Rock && player2Choice == Choice.Scissors) ||
                     (player1Choice == Choice.Paper && player2Choice == Choice.Rock) ||
                     (player1Choice == Choice.Scissors && player2Choice == Choice.Paper))
            {
                result = Result.Player1Win;
                if (resultLabel != null)
                    resultLabel.Text = "You Win!";
                GD.Print("RPS result: Player1Win");
            }
            else
            {
                result = Result.Player2Win;
                if (resultLabel != null)
                    resultLabel.Text = "Computer Wins!";
                GD.Print("RPS result: Player2Win");
            }
            
            // Wait a moment before emitting the signal
            var timer = GetTree().CreateTimer(1.5);
            timer.Timeout += () => EmitResultSignal(result);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Error in DetermineResult: {ex.Message}");
            // Force a default result
            var timer = GetTree().CreateTimer(0.5);
            timer.Timeout += () => EmitResultSignal(Result.Draw);
        }
    }
    
    private void EmitResultSignal(Result result)
    {
        // Skip if being destroyed to prevent crashes
        if (isBeingDestroyed)
        {
            GD.Print("Skipping signal emission because scene is being destroyed");
            return;
        }
            
        try
        {
            // Emit the signal safely
            GD.Print($"Emitting GameFinished signal with result: {result}");
            EmitSignal(SignalName.GameFinished, (int)result);
        }
        catch (Exception ex)
        {
            GD.PrintErr("Error emitting RPS result signal: " + ex.Message);
            // Force a default result if signal emission fails
            if (!isBeingDestroyed)
            {
                // Try to remove ourselves from the tree to recover
                GD.Print("Attempting to recover by freeing the scene");
                QueueFree();
            }
        }
    }
    
    // Ensure the RPS game doesn't hang if the user doesn't make a choice
    // This safety mechanism auto-plays if 10 seconds pass
    public void _on_auto_play_timer_timeout()
    {
        if (player1Choice == Choice.None && !isBeingDestroyed)
        {
            GD.Print("AutoPlayTimer triggered - auto-selecting Rock");
            // Auto-select rock if player hasn't chosen
            SetPlayer1Choice(Choice.Rock);
        }
        else
        {
            GD.Print("AutoPlayTimer triggered but choice already made or scene destroyed");
        }
    }
}