using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using static Board;

public class EngineConnector
{
    // engine process related

    private Process engineProcess = new Process();
    private StreamReader engineProcessStdOut;
    private StreamWriter engineProcessStdIn;

    // list with all the moves

    private List<string> moves = new List<string>();

    // fen string, move time ...

    private string fenString = StartFEN;
    public int MoveTime = 1000; // in ms

    // for multithreading

    private System.Threading.Mutex mutex = new System.Threading.Mutex();

    // connect to a chess engine

    public void ConnectToEngine(string enginePath)
    {
        // engine process start info

        engineProcess.StartInfo.FileName = enginePath;
        engineProcess.StartInfo.UseShellExecute = false;
        engineProcess.StartInfo.RedirectStandardOutput = true;
        engineProcess.StartInfo.RedirectStandardInput = true;
        engineProcess.StartInfo.CreateNoWindow = true;

        // start engine process
        
        try
        {
            engineProcess.Start();

            // set std input and output of the child process

            engineProcessStdOut = engineProcess.StandardOutput;
            engineProcessStdIn = engineProcess.StandardInput;

            GD.Print("Connected to engine: ", enginePath);
        }
        catch (Exception e)
        {
            GD.Print(e.Message);
        }
    }

    public void Disconnect()
    {
        // send quit to engine and wait for exit

        mutex.WaitOne();
        {
            engineProcessStdIn.WriteLine("quit");
        }
        mutex.ReleaseMutex();

        engineProcess.WaitForExit();

        GD.Print("Disconnected from engine");
    }

    public void LimitStrengthTo(int eloValue)
    {
        mutex.WaitOne();
        {
            if (eloValue != int.MaxValue)
            {
                string command = string.Format("setoption name UCI_LimitStrength value true\nsetoption name UCI_Elo value {0}\n", eloValue);
                engineProcessStdIn.WriteLine(command);
            }
            else
            {
                engineProcessStdIn.WriteLine("setoption name UCI_LimitStrength value false\n");
            }
        }
        mutex.ReleaseMutex();
    }

    public void StopCalculating()
    {
        mutex.WaitOne();
        {
            engineProcessStdIn.WriteLine("stop");
        }
        mutex.ReleaseMutex();
    }

    public void LoadFEN(string fen)
    {
        fenString = fen;
        moves.Clear();
    }

    private string FromMoveToString(Move move)
    {
        // notation
        Dictionary<Piece.Type, char> symbolFromPieceType = new Dictionary<Piece.Type, char>()
        {
            { Piece.Type.Pawn, 'p' }, { Piece.Type.Knight, 'n' }, { Piece.Type.Bishop, 'b' },
            { Piece.Type.Rook, 'r' }, { Piece.Type.Queen , 'q' }, { Piece.Type.King  , 'k' }
        };

        // Check if the piece type is None, which would be invalid
        if (move.pieceSource.type == Piece.Type.None)
        {
            GD.PrintErr("Cannot convert invalid move to string: source piece is None");
            return "a1a1"; // Return a dummy move as fallback
        }

        char[] letters = { 'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h' };

        // get squares
        int squareSourceI = move.squareSourceIndex % 8;
        int squareSourceJ = move.squareSourceIndex / 8;
        int squareTargetI = move.squareTargetIndex % 8;
        int squareTargetJ = move.squareTargetIndex / 8;

        // str move
        string strMove = string.Format("{0}{1}{2}{3}", letters[squareSourceI], 8 - squareSourceJ, letters[squareTargetI], 8 - squareTargetJ);

        // if needs to add the promotion piece
        if (move.flags == Move.Flags.Promotion)
        {
            // Make sure promotionPieceType is valid
            if (move.promotionPieceType != Piece.Type.None && symbolFromPieceType.ContainsKey(move.promotionPieceType))
            {
                strMove += symbolFromPieceType[move.promotionPieceType];
            }
            else
            {
                // Default to queen if no valid promotion piece type
                strMove += 'q';
            }
        }

        // return the move
        return strMove;
    }

    private Move FromStringToMove(Board board, string strMove)
    {
        Dictionary<char, Piece.Type> pieceTypeFromSymbol = new Dictionary<char, Piece.Type>()
        {
            { 'p', Piece.Type.Pawn }, { 'n', Piece.Type.Knight }, { 'b', Piece.Type.Bishop },
            { 'r', Piece.Type.Rook }, { 'q', Piece.Type.Queen  }, { 'k', Piece.Type.King   }
        };

        // Make sure we have a valid move string
        if (string.IsNullOrEmpty(strMove) || strMove.Length < 4)
        {
            GD.PrintErr("Invalid move string received from engine: ", strMove);
            return new Move(); // Return empty move
        }

        try
        {
            // source tile
            int squareSourceI = strMove[0] - 'a';
            int squareSourceJ = 7 - (strMove[1] - '1');
            int squareSourceIndex = squareSourceI + squareSourceJ * 8;

            // targetTile
            int squareTargetI = strMove[2] - 'a';
            int squareTargetJ = 7 - (strMove[3] - '1');
            int squareTargetIndex = squareTargetI + squareTargetJ * 8;

            // Get the piece at the source square
            Piece sourcePiece = board.GetPiece(squareSourceIndex);
            
            // If there's no piece at the source location, something is wrong
            if (sourcePiece.type == Piece.Type.None)
            {
                GD.PrintErr("No piece found at source position for move: ", strMove);
                
                // Try to find any piece of the current turn color that can move to the target
                Piece.Color currentTurnColor = board.GetTurnColor();
                List<int> pieceIndices = board.GetPiecesIndicesByColor(currentTurnColor);
                
                foreach (int pieceIndex in pieceIndices)
                {
                    List<Move> pieceMoves = MoveGeneration.GetPseudoLegalMoves(board, pieceIndex);
                    foreach (Move move in pieceMoves)
                    {
                        if (move.squareTargetIndex == squareTargetIndex)
                        {
                            GD.Print("Found alternative move with same target");
                            return move;
                        }
                    }
                }
                
                // If we reach here, couldn't find a suitable move
                GD.PrintErr("Could not find a suitable move - this may happen after RPS");
                return new Move();
            }

            // get moves for the piece at source location
            List<Move> moves = MoveGeneration.GetPseudoLegalMoves(board, squareSourceIndex);
            Move chosenMove = new Move();
            bool moveFound = false;

            foreach (Move move in moves)
            {
                if (move.squareTargetIndex == squareTargetIndex)
                {
                    chosenMove = move;
                    moveFound = true;
                    break;
                }
            }

            // If no move was found, create a basic move
            if (!moveFound)
            {
                GD.Print("Move not found in pseudo-legal moves - creating basic move");
                Piece targetPiece = board.GetPiece(squareTargetIndex);
                chosenMove = new Move
                {
                    squareSourceIndex = squareSourceIndex,
                    squareTargetIndex = squareTargetIndex,
                    pieceSource = sourcePiece,
                    pieceTarget = targetPiece
                };
            }

            // Handle promotion if needed
            if (strMove.Length > 4 && 
                ((sourcePiece.type == Piece.Type.Pawn && 
                  ((sourcePiece.color == Piece.Color.White && squareTargetJ == 0) || 
                   (sourcePiece.color == Piece.Color.Black && squareTargetJ == 7)))))
            {
                chosenMove.flags = Move.Flags.Promotion;
                chosenMove.promotionPieceType = pieceTypeFromSymbol[strMove[4]];
            }

            return chosenMove;
        }
        catch (Exception ex)
        {
            GD.PrintErr("Error processing move: ", strMove, ", Error: ", ex.Message);
            return new Move();
        }
    }

    // Special method to resync the engine's board state after RPS
    public void ResyncBoardState(Board board)
    {
        // We'll update the engine's position using the current board state
        fenString = board.GetFEN();
        moves.Clear();
        
        // Send the new position to the engine
        mutex.WaitOne();
        {
            // Issue a position command with the current FEN
            engineProcessStdIn.WriteLine($"position fen {fenString}");
        }
        mutex.ReleaseMutex();
        
        GD.Print("Resynced engine board state after RPS game");
    }

    public void SendMove(Move move)
    {
        try
        {
            // Check if this is a valid move before sending to engine
            if (move.squareSourceIndex < 0 || move.squareSourceIndex > 63 ||
                move.squareTargetIndex < 0 || move.squareTargetIndex > 63)
            {
                GD.PrintErr($"Invalid move indices: source={move.squareSourceIndex}, target={move.squareTargetIndex}. Ignoring.");
                return;
            }
            
            if (move.pieceSource.type == Piece.Type.None)
            {
                GD.PrintErr("Cannot send move with None piece type. Ignoring.");
                return;
            }
            
            try
            {
                string strMove = FromMoveToString(move);
                moves.Add(strMove);
                GD.Print("move: ", strMove);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"Error converting move to string: {ex.Message}. Using dummy move.");
                // Add a dummy move instead
                moves.Add("a1a1");
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Unhandled exception in SendMove: {ex.Message}");
        }
    }

    public Move GetBestMove(Board board)
    {
        try
        {
            // construct the moves string
            StringBuilder command = new StringBuilder();

            command.AppendFormat("position fen {0} moves", fenString);

            foreach (string strMove in moves)
            {
                command.AppendFormat(" {0}", strMove);
            }

            command.AppendFormat("\ngo movetime {0}\n", MoveTime);

            // write to the engine process std in
            mutex.WaitOne();
            {
                engineProcessStdIn.Write(command);
            }
            mutex.ReleaseMutex();

            // read the output from the engine until it found the move
            bool moveFound = false;
            string bestMoveString = null;
            int timeoutCounter = 0;
            const int maxTimeoutCount = 10000; // Prevent infinite loop
            
            do
            {
                try
                {
                    if (!engineProcess.HasExited)
                    {
                        // Safer way to check for available data
                        bool hasData = false;
                        try
                        {
                            hasData = engineProcessStdOut.Peek() >= 0;
                        }
                        catch (IOException)
                        {
                            // Stream might be closed - handle gracefully
                            GD.PrintErr("Stream error when peeking - engine might have closed the stream");
                            return FallbackMove(board);
                        }
                        
                        if (hasData)
                        {
                            string engineOutputLine = engineProcessStdOut.ReadLine();
                            
                            if (engineOutputLine != null && engineOutputLine.Contains("bestmove"))
                            {
                                string[] bestMoveLine = engineOutputLine.Split(' ');
                                if (bestMoveLine.Length > 1)
                                {
                                    bestMoveString = bestMoveLine[1];
                                    if (bestMoveString != "(none)" && bestMoveString != "0000")
                                    {
                                        moveFound = true;
                                    }
                                    else
                                    {
                                        // No legal moves or engine issue - return fallback move
                                        GD.PrintErr("Engine returned no legal moves");
                                        return FallbackMove(board);
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        GD.PrintErr("Engine process has exited unexpectedly");
                        return FallbackMove(board);
                    }
                }
                catch (Exception ex)
                {
                    GD.PrintErr("Error reading from engine: ", ex.Message);
                    return FallbackMove(board);
                }
                
                timeoutCounter++;
                if (timeoutCounter > maxTimeoutCount)
                {
                    GD.PrintErr("Timeout waiting for engine response");
                    return FallbackMove(board);
                }
                
                // Small sleep to prevent CPU overuse
                System.Threading.Thread.Sleep(1);
                
            } while (!moveFound);

            // from bestmovestring to actual move
            Move bestMove = FromStringToMove(board, bestMoveString);
            
            // Check if the move is valid
            if (bestMove.pieceSource.type == Piece.Type.None)
            {
                // Invalid move - use fallback
                GD.PrintErr("Invalid move returned by engine - using fallback");
                return FallbackMove(board);
            }

            return bestMove;
        }
        catch (Exception ex)
        {
            GD.PrintErr("Error in GetBestMove: ", ex.Message);
            return FallbackMove(board);
        }
    }
    
    // Helper method to find a fallback move when engine fails
    private Move FallbackMove(Board board)
    {
        // Try to find any legal move
        List<int> pieceIndices = board.GetPiecesIndicesByColor(board.GetTurnColor());
        foreach (int pieceIndex in pieceIndices)
        {
            List<Move> legalMoves = MoveGeneration.GetLegalMoves(board, pieceIndex);
            if (legalMoves.Count > 0)
            {
                GD.Print("Using fallback legal move");
                return legalMoves[0];  // Return the first legal move we find
            }
        }
        
        // If still no move found, return empty move
        return new Move();
    }
}
