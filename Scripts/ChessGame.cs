using Godot;
using System;
using System.Collections.Generic;
using System.Threading;
using static Board;

public partial class ChessGame : Node
{
	private enum GameState
	{
		AnimateMove,
		WaitingForAnimationToFinish,
		AnimationJustFinished,
		WaitingForComputer,
		PlayerMoving,
		GameEnd,
		PlayingRPS  // New state for Rock Paper Scissors
	}

	private struct PieceSelection
	{
		public bool isSelected;
		public bool isHolding;
		public int squareIndex;
		public List<Move> legalMoves;
	}

	private struct MoveAnimation
	{
		public Move move;
		public bool dragged;
	}

	private static readonly string checkmateString = "Checkmate!";
	private static readonly string drawString = "Draw!";

	// theme

	[Export]
	private Texture2D piecesTextureAtlas;
	[Export]
	private BoardTheme boardTheme;

	[Export]
	private int computerElo = 1700;

	[Export]
	private Label gameEndLabel; // shows why the game ended (checkmate or stalemate)

	[Export]
	private PackedScene rpsScene; // RPS game scene

	private Board board = new Board();
	private BoardGraphics boardGraphics;
	private Dictionary<string, AudioStreamPlayer> sounds = new Dictionary<string, AudioStreamPlayer>();

	private PieceSelection pieceSelection;
	private MoveAnimation moveAnimation;
	private Piece.Color playerColor = Piece.Color.White;
	private GameState gameState = GameState.PlayerMoving;

	// computer

	private bool engineRunning = true;
	private EngineConnector engineConnector = new EngineConnector();
	private Thread computerThread;
	private System.Threading.Semaphore computerThreadBarrier = new System.Threading.Semaphore(0, 1);
	private System.Threading.Mutex mutex = new System.Threading.Mutex();
	private System.Threading.Mutex engineConnectorMutex = new System.Threading.Mutex();

	// RPS game
	private RockPaperScissors rpsGame;
	private Move pendingCaptureMove;
	private bool isRPSActive = false;
	private bool preserveTurnAfterRPS = false;
	private Piece.Color rpsWinnerColor = Piece.Color.None;

	// select & deselect piece

	private void SelectPiece(int squareIndex, List<Move> legalMoves)
	{
		pieceSelection.isSelected = true;
		pieceSelection.isHolding = true;
		pieceSelection.squareIndex = squareIndex;
		pieceSelection.legalMoves = legalMoves;
	}

	private void DeselectPiece()
	{
		pieceSelection.isSelected = false;
		pieceSelection.isHolding = false;
	}

	// plays as selected color with the selected fen

	public void PlayAsColor(string fen, Piece.Color color)
	{
		// validate fen string

		if (!FenValidator.IsFenStringValid(fen))
		{
			GD.Print(fen, " is not a valid fen string");
			return;
		}

		// send stop command to engine

		engineConnector.StopCalculating();

		// change from white to black

		mutex.WaitOne();
		{
			// change human color

			playerColor = color;

			// load fen

			board.LoadFEN(fen);

			engineConnectorMutex.WaitOne();
			{
				engineConnector.LoadFEN(fen);
			}
			engineConnectorMutex.ReleaseMutex();

			// update board ui

			DeselectPiece();
			boardGraphics.DeselectSquare();
			boardGraphics.StopAnimation();
			boardGraphics.FlipBoard(color == Piece.Color.Black);
			boardGraphics.UpdateSprites();

			// change game state

			gameState = GameState.PlayerMoving;
		}
		mutex.ReleaseMutex();

		// hide game end label

		gameEndLabel.Visible = false;

		// play sound

		sounds["GameStart"].Play();
	}

	public void SelectPromotionPieceType(Piece.Type type)
	{
		mutex.WaitOne();
		{
			board.PromotionPieceType = type;
		}
		mutex.ReleaseMutex();
	}

	public string GetFEN()
	{
		string fen;

		mutex.WaitOne();
		{
			fen = board.GetFEN();
		}
		mutex.ReleaseMutex();

		return fen;
	}

	public void SelectComputerELO(int elo)
	{
		engineConnector.LimitStrengthTo(elo);
	}

	public void FlipBoard()
	{
		boardGraphics.FlipBoard(!boardGraphics.IsBoardFlipped());
		boardGraphics.UpdateSprites();
	}

	// computer turn (this runs in a separated thread)

	private void ComputerTurn()
	{
		Board boardCopy = new Board();

		while (true)
		{
			// non busy wait until computer turn
			computerThreadBarrier.WaitOne();

			// check if in the middle of the move calculation a button that aborts this current operation is pressed
			bool aborted;

			// perform a copy of the current state of the board
			mutex.WaitOne();
			{
				aborted = gameState != GameState.WaitingForComputer;

				if (!aborted)
				{
					board.CopyBoardState(boardCopy);
				}
			}
			mutex.ReleaseMutex();

			if (!aborted)
			{
				try
				{
					// Get the chosen move using the copy of the board
					Move move;
					
					GD.Print("Computer is thinking...");
					
					engineConnectorMutex.WaitOne();
					{
						move = engineConnector.GetBestMove(boardCopy);
					}
					engineConnectorMutex.ReleaseMutex();
					
					GD.Print($"Computer chose move: {move.pieceSource.type} from {move.squareSourceIndex} to {move.squareTargetIndex}");
					
					if (move.pieceTarget.type != Piece.Type.None) {
						GD.Print($"Computer is trying to capture {move.pieceTarget.type}");
					}

					// Setup the move animation
					mutex.WaitOne();
					{
						aborted = gameState != GameState.WaitingForComputer;

						if (!aborted)
						{
							// Check if the computer move is a capture
							if (move.pieceTarget.type != Piece.Type.None)
							{
								try
								{
									GD.Print("Computer is attempting a capture - setting up RPS game");
									
									// Store the pending capture move
									pendingCaptureMove = move;
									
									// Create RPS scene
									if (rpsScene == null)
									{
										// Load the scene dynamically if not assigned in the editor
										GD.Print("Loading RPS scene...");
										rpsScene = GD.Load<PackedScene>("res://Scenes/RPS/RockPaperScissors.tscn");
										
										// If still null after attempting to load, just make the move normally
										if (rpsScene == null)
										{
											GD.PrintErr("Failed to load RockPaperScissors scene. Performing normal capture.");
											moveAnimation.move = move;
											moveAnimation.dragged = false;
											gameState = GameState.AnimateMove;
											mutex.ReleaseMutex();
											continue;
										}
									}
									
									GD.Print("Creating RPS game instance...");
									// Create the RPS game instance
									rpsGame = rpsScene.Instantiate<RockPaperScissors>();
									
									// Set capture information
									GD.Print("Setting capture move data...");
									rpsGame.SetCaptureMove(move, board.GetTurnColor());
									
									// Connect using Godot's signal connection method 
									// We use a try-catch to prevent crashes if signal connection fails
									try {
										GD.Print("Connecting RPS signal...");
										rpsGame.Connect(RockPaperScissors.SignalName.GameFinished, Callable.From<int>(OnRPSGameFinished));
										GD.Print("Signal connected successfully");
									}
									catch (Exception ex) {
										GD.PrintErr("Error connecting RPS signal: " + ex.Message);
										// If signal connection fails, we'll just do a normal capture
										moveAnimation.move = move;
										moveAnimation.dragged = false;
										gameState = GameState.AnimateMove;
										mutex.ReleaseMutex();
										continue;
									}
									
									GD.Print("Adding RPS scene to tree...");
									CallDeferred("add_child", rpsGame);
									
									// Change game state
									GD.Print("Changing game state to PlayingRPS");
									gameState = GameState.PlayingRPS;
									isRPSActive = true;
								}
								catch (Exception ex)
								{
									// If anything fails during RPS setup, revert to normal move
									GD.PrintErr("Error in RPS game setup: " + ex.Message + "\n" + ex.StackTrace);
									moveAnimation.move = move;
									moveAnimation.dragged = false;
									gameState = GameState.AnimateMove;
								}
							}
							else
							{
								// Regular non-capture move
								GD.Print("Computer making regular move (non-capture)");
								moveAnimation.move = move;
								moveAnimation.dragged = false;
								gameState = GameState.AnimateMove;
							}
						}
					}
					mutex.ReleaseMutex();
				}
				catch (Exception ex)
				{
					GD.PrintErr($"Unhandled exception in ComputerTurn: {ex.Message}\n{ex.StackTrace}");
					
					mutex.WaitOne();
					{
						// Try to recover from error state
						if (gameState == GameState.WaitingForComputer)
						{
							gameState = GameState.PlayerMoving;
						}
					}
					mutex.ReleaseMutex();
				}
			}
		}
	}

	// Process a move selected by the player
	private void ProcessPlayerMove(Move move, bool isDragged)
	{
		// If the move is a capture, trigger Rock Paper Scissors
		if (move.pieceTarget.type != Piece.Type.None)
		{
			// Store the pending capture move
			pendingCaptureMove = move;
			
			// Create RPS scene
			if (rpsScene == null)
			{
				// Load the scene dynamically if not assigned in the editor
				rpsScene = GD.Load<PackedScene>("res://Scenes/RPS/RockPaperScissors.tscn");
				
				// If still null after attempting to load, just make the move normally
				if (rpsScene == null)
				{
					GD.PrintErr("Failed to load RockPaperScissors scene. Performing normal capture.");
					moveAnimation.move = move;
					moveAnimation.dragged = isDragged;
					gameState = GameState.AnimateMove;
					return;
				}
			}
			
			// Create the RPS game instance
			rpsGame = rpsScene.Instantiate<RockPaperScissors>();
			rpsGame.SetCaptureMove(move, playerColor);
			// Connect using Godot's signal connection method
			rpsGame.Connect(RockPaperScissors.SignalName.GameFinished, Callable.From<int>(OnRPSGameFinished));
			AddChild(rpsGame);
			
			// Change game state
			gameState = GameState.PlayingRPS;
			isRPSActive = true;
		}
		else
		{
			// Not a capture, just do the normal move
			moveAnimation.move = move;
			moveAnimation.dragged = isDragged;
			gameState = GameState.AnimateMove;
		}
	}

	// Handle the result of the RPS game
	private void OnRPSGameFinished(int resultValue)
	{
		// Convert the integer result to the RockPaperScissors.Result enum
		RockPaperScissors.Result result = (RockPaperScissors.Result)resultValue;
		
		// Remove RPS scene
		rpsGame.QueueFree();
		isRPSActive = false;
		
		Move finalMove = pendingCaptureMove;
		
		// Store original turn color to determine if we need to preserve it
		Piece.Color originalTurnColor = board.GetTurnColor();
		
		if (result == RockPaperScissors.Result.Player1Win || result == RockPaperScissors.Result.Draw)
		{
			// Player wins or draws - normal capture proceeds
			GD.Print("Player won RPS - normal capture");
			// The winner gets another turn, so we'll need to preserve the current turn
		}
		else if (result == RockPaperScissors.Result.Player2Win)
		{
			// Computer wins - swap the pieces!
			GD.Print("Computer won RPS - piece swap");
			
			try
			{
				// Create a modified move where the target captures the source
				finalMove = new Move
				{
					squareSourceIndex = pendingCaptureMove.squareTargetIndex,
					squareTargetIndex = pendingCaptureMove.squareSourceIndex,
					pieceSource = pendingCaptureMove.pieceTarget,
					pieceTarget = pendingCaptureMove.pieceSource,
					flags = pendingCaptureMove.flags
				};
				
				// The defender wins, so turn color should switch to the defender's color
				// This is crucial for making sure Stockfish moves the correct piece
				originalTurnColor = pendingCaptureMove.pieceTarget.color;
				
				// Validate the move is at least physically possible (on the board)
				if (finalMove.squareSourceIndex < 0 || finalMove.squareSourceIndex > 63 ||
					finalMove.squareTargetIndex < 0 || finalMove.squareTargetIndex > 63)
				{
					GD.PrintErr("RPS resulted in an invalid move - using original capture instead");
					finalMove = pendingCaptureMove;
				}
			}
			catch (Exception ex)
			{
				GD.PrintErr("Error creating RPS result move: " + ex.Message);
				// If there's any error, fall back to the original move
				finalMove = pendingCaptureMove;
			}
		}
		
		// Execute the final move
		moveAnimation.move = finalMove;
		moveAnimation.dragged = false;
		
		// Flag to preserve the turn after RPS
		preserveTurnAfterRPS = true;
		rpsWinnerColor = originalTurnColor;
		
		gameState = GameState.AnimateMove;

		// Resync the engine with the current board state
		engineConnector.ResyncBoardState(board);
	}

	// Called when the node enters the scene tree for the first time.

	public override void _Ready()
	{
		// precalculate moves

		MoveGeneration.PrecalculateMoves();

		// get nodes

		boardGraphics = GetNode<BoardGraphics>("BoardGraphics");

		sounds["MoveSelf"] = GetNode<AudioStreamPlayer>("MoveSelfSound");
		sounds["MoveOpponent"] = GetNode<AudioStreamPlayer>("MoveOpponentSound");
		sounds["Capture"] = GetNode<AudioStreamPlayer>("CaptureSound");
		sounds["Castle"] = GetNode<AudioStreamPlayer>("CastleSound");
		sounds["Check"] = GetNode<AudioStreamPlayer>("CheckSound");
		sounds["Promote"] = GetNode<AudioStreamPlayer>("PromoteSound");
		sounds["GameStart"] = GetNode<AudioStreamPlayer>("GameStartSound");
		sounds["GameEnd"] = GetNode<AudioStreamPlayer>("GameEndSound");

		// connect board graphics to board

		boardGraphics.LoadPiecesTheme(piecesTextureAtlas);
		boardGraphics.LoadBoardTheme(boardTheme);
		boardGraphics.ConnectToBoard(board);

		// engine connector

		engineConnector.ConnectToEngine("Assets/ChessEngines/stockfish/stockfish.exe");
		engineConnector.LimitStrengthTo(computerElo);

		// load fens 4Q3/2B2Pp1/p5kp/P7/4q3/b1p4P/5PPK/4r3 w - -

		PlayAsColor(StartFEN, playerColor);

		// computer thread

		computerThread = new Thread(ComputerTurn);
		computerThread.Start();
	}

	// process notifications

	public override void _Notification(int what)
	{
		if (what == NotificationWMCloseRequest)
		{
			engineConnector.Disconnect();
		}
	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.

	public override void _Process(double delta)
	{
		try
		{
			mutex.WaitOne();

			switch (gameState)
			{
				case GameState.AnimateMove:
					try
					{
						GD.Print($"Processing AnimateMove state - move: {moveAnimation.move.pieceSource.type} from {moveAnimation.move.squareSourceIndex} to {moveAnimation.move.squareTargetIndex}");
						
						// make the move and handle RPS-related turn logic
						if (preserveTurnAfterRPS)
						{
							GD.Print("Preserving turn after RPS");
							// Store the original turn color
							Piece.Color originalTurnColor = board.GetTurnColor();
							
							// Execute the move
							GD.Print("Making move on board after RPS");
							board.MakeMove(moveAnimation.move);
							
							// Force the turn color to match the RPS winner
							GD.Print($"Setting turn color to RPS winner: {rpsWinnerColor}");
							board.SetTurnColor(rpsWinnerColor);
							
							// Reset the flag
							preserveTurnAfterRPS = false;
							rpsWinnerColor = Piece.Color.None;
							
							// Send move to engine
							GD.Print("Sending move to engine after RPS");
							try
							{
								engineConnector.SendMove(moveAnimation.move);
							}
							catch (Exception ex)
							{
								GD.PrintErr($"Error sending move to engine: {ex.Message}\n{ex.StackTrace}");
							}
						}
						else
						{
							// Normal move execution
							GD.Print("Normal move execution (not after RPS)");
							board.MakeMove(moveAnimation.move);
							
							try
							{
								GD.Print("Sending normal move to engine");
								engineConnector.SendMove(moveAnimation.move);
							}
							catch (Exception ex)
							{
								GD.PrintErr($"Error sending move to engine: {ex.Message}\n{ex.StackTrace}");
							}
						}

						// board graphics set up
						GD.Print("Updating board graphics");
						boardGraphics.DeselectPieceSquare();
						boardGraphics.DeselectSquare();
						boardGraphics.SetHintMoves(null);

						// perform the move animation for the selected move
						GD.Print("Starting move animation");
						boardGraphics.AnimateMove(moveAnimation.move, moveAnimation.dragged, Callable.From(() =>
						{
							GD.Print("Animation finished callback");
							gameState = GameState.AnimationJustFinished;
						}));

						// change state
						GD.Print("Changing state to WaitingForAnimationToFinish");
						gameState = GameState.WaitingForAnimationToFinish;
					}
					catch (Exception ex)
					{
						GD.PrintErr($"Error in AnimateMove state: {ex.Message}\n{ex.StackTrace}");
						// Try to recover gracefully
						gameState = GameState.PlayerMoving;
					}
					break;
				case GameState.AnimationJustFinished:
					// update sprites

					boardGraphics.UpdateSprites();

					// check for game ended 

					List<Move> availableMoves = MoveGeneration.GetAllLegalMovesByColor(board, board.GetTurnColor());
					bool isKingInCheck = MoveGeneration.IsKingInCheck(board, board.GetTurnColor());

					if (availableMoves.Count <= 0) // if there are no legal moves
					{
						if (isKingInCheck) // if is the king in check then it is checkmate
						{
							gameEndLabel.Text = checkmateString;
							gameEndLabel.Visible = true;
							GD.Print("Checkmate!");
						}
						else // it is stalemate
						{
							gameEndLabel.Text = drawString;
							gameEndLabel.Visible = true;
							GD.Print("Draw!");
						}

						sounds["GameEnd"].Play();

						// change game state

						gameState = GameState.GameEnd;
					}
					else if (isKingInCheck) // if the king is checked
					{
						sounds["Check"].Play();

						// change game state

						gameState = GameState.PlayerMoving;
					}
					else
					{
						// play sounds

						switch (moveAnimation.move.flags)
						{
							case Move.Flags.CastleShort:
							case Move.Flags.CastleLong:
								sounds["Castle"].Play();
								break;
							case Move.Flags.Promotion:
								sounds["Promote"].Play();
								break;
							case Move.Flags.EnPassant:
								sounds["Capture"].Play();
								break;
							default:
								if (moveAnimation.move.pieceTarget.type != Piece.Type.None)
								{
									sounds["Capture"].Play();
								}
								else if (moveAnimation.move.pieceSource.color == playerColor)
								{
									sounds["MoveSelf"].Play();
								}
								else
								{
									sounds["MoveOpponent"].Play();
								}
								break;
						}

						// change game state

						gameState = GameState.PlayerMoving;
					}
					break;
				case GameState.PlayingRPS:
					// Processing is handled by the RPS scene
					break;
				case GameState.PlayerMoving:
					// get the turn color

					Piece.Color turnColor = board.GetTurnColor();

					if (turnColor == playerColor)
					{
						// human turn

						// mouse position relative to the board graphics

						Vector2 mousePosition = GetViewport().GetMousePosition() - boardGraphics.Position;

						// get the square the mouse is at & calculate its index

						bool isSquareInBoard = boardGraphics.TryGetSquareIndexFromCoords(mousePosition, out int squareIndex);

						// select & deselect piece & make moves

						if (Input.IsActionJustPressed("Click"))
						{
							if (isSquareInBoard)
							{
								if (pieceSelection.isSelected)
								{
									// check if it is in a legal move

									bool isMoveLegal = false;

									foreach (Move move in pieceSelection.legalMoves)
									{
										if (move.squareTargetIndex == squareIndex)
										{
											// mark the move legal flag to true

											isMoveLegal = true;

											// deselect the piece

											DeselectPiece();

											// process the move with RPS if needed
											ProcessPlayerMove(move, false);

											break;
										}
									}

									// check if the move is legal

									if (!isMoveLegal)
									{
										// check if other piece is selected

										Piece piece = board.GetPiece(squareIndex);

										if (piece.color == playerColor)
										{
											// get legal moves from the piece

											List<Move> legalMoves = MoveGeneration.GetLegalMoves(board, squareIndex);

											// select the piece

											SelectPiece(squareIndex, legalMoves);

											// board select the piece

											boardGraphics.SelectPiece(squareIndex);
											boardGraphics.SelectPieceSquare(squareIndex);
											boardGraphics.SetHintMoves(legalMoves);
										}
										else
										{
											// deselect the piece

											DeselectPiece();

											// remove the board hints & deselect the piece

											boardGraphics.DeselectPiece(squareIndex);
											boardGraphics.DeselectPieceSquare();
											boardGraphics.SetHintMoves(null);
										}
									}
								}
								else
								{
									Piece piece = board.GetPiece(squareIndex);

									if (piece.color == playerColor)
									{
										// get legal moves from the piece

										List<Move> legalMoves = MoveGeneration.GetLegalMoves(board, squareIndex);

										// select the piece

										SelectPiece(squareIndex, legalMoves);

										// board select the piece

										boardGraphics.SelectPiece(squareIndex);
										boardGraphics.SelectPieceSquare(squareIndex);
										boardGraphics.SetHintMoves(legalMoves);
									}
								}
							}
							else
							{
								// check if there was a piece selected

								if (pieceSelection.isSelected)
								{
									// deselect the piece

									DeselectPiece();

									// remove the board hints & deselect the piece

									boardGraphics.DeselectPiece(pieceSelection.squareIndex);
									boardGraphics.DeselectPieceSquare();
									boardGraphics.SetHintMoves(null);
								}
							}
						}

						// when the piece is being hold with the mouse

						if (pieceSelection.isHolding)
						{
							Sprite2D pieceSprite = boardGraphics.GetPieceSprite(pieceSelection.squareIndex);
							pieceSprite.Position = mousePosition;

							if (isSquareInBoard)
							{
								boardGraphics.SelectSquare(squareIndex);
							}
						}
						else
						{
							boardGraphics.DeselectSquare();
						}

						// when the mouse is released (if the piece was being hold things happen)

						if (Input.IsActionJustReleased("Click"))
						{
							if (pieceSelection.isHolding)
							{
								if (isSquareInBoard)
								{
									// check if it is in a legal move

									bool isMoveLegal = false;

									foreach (Move move in pieceSelection.legalMoves)
									{
										if (move.squareTargetIndex == squareIndex)
										{
											// mark the move legal flag to true

											isMoveLegal = true;

											// deselect the piece

											DeselectPiece();

											// process the move with RPS if needed
											ProcessPlayerMove(move, true);

											break;
										}
									}

									if (!isMoveLegal)
									{
										pieceSelection.isHolding = false;
										boardGraphics.UpdateSprites();
									}
								}
								else
								{
									pieceSelection.isHolding = false;
									boardGraphics.UpdateSprites();
								}
							}
						}
					}
					else
					{
						// computer turn

						gameState = GameState.WaitingForComputer;
						computerThreadBarrier.Release();
					}
					break;
			}

			mutex.ReleaseMutex();
		}
		catch (Exception ex)
		{
			// Make sure we release the mutex even if an exception occurs
			if (mutex != null)
			{
				try { mutex.ReleaseMutex(); } catch { }
			}
			
			GD.PrintErr($"Unhandled exception in _Process: {ex.Message}\n{ex.StackTrace}");
		}
	}
}