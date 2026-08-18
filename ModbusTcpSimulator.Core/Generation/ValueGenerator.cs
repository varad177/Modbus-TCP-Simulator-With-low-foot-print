using ModbusTcpSimulator.Core.Models;

namespace ModbusTcpSimulator.Core.Generation;

/// <summary>
/// Stateful value generator for a single RegisterConfiguration.
/// Each instance tracks the current generated value and advances it on each tick.
/// </summary>
public sealed class ValueGenerator
{
    private readonly RegisterConfiguration _cfg;
    private readonly Random _rng;
    private double _currentValue;
    private double _phase;          // for sine wave
    private long _tick;

    public ValueGenerator(RegisterConfiguration cfg)
    {
        _cfg = cfg;
        _rng = new Random();
        _currentValue = cfg.InitialValue;
        _phase = 0;
        _tick = 0;
    }

    /// <summary>Produce the next value according to generation strategy.</summary>
    public double Next(bool clampToRange = true)
    {
        _tick++;
        double baseValue = Generate();
        baseValue = ApplyScatterness(baseValue);
        if (clampToRange && _cfg.MinValue < _cfg.MaxValue)
            baseValue = Math.Clamp(baseValue, _cfg.MinValue, _cfg.MaxValue);
        return baseValue;
    }

    private double Generate()
    {
        if (_cfg.DataType == DataType.Bool)
        {
            return _cfg.GenerationType switch
            {
                GenerationType.Constant => _cfg.ConstantValue != 0 ? 1.0 : 0.0,
                GenerationType.Random => _rng.Next(0, 2),
                GenerationType.Increment or GenerationType.Decrement => _tick % 2 == 0 ? 1.0 : 0.0,
                _ => 1.0
            };
        }

        switch (_cfg.GenerationType)
        {
            case GenerationType.Constant:
                return _cfg.ConstantValue;

            case GenerationType.Random:
                return _rng.NextDouble() * (_cfg.MaxValue - _cfg.MinValue) + _cfg.MinValue;

            case GenerationType.Increment:
                _currentValue += _cfg.IncrementStep;
                if (_cfg.MaxValue > _cfg.MinValue && _currentValue > _cfg.MaxValue)
                    _currentValue = _cfg.MinValue;
                return _currentValue;

            case GenerationType.Decrement:
                _currentValue -= _cfg.IncrementStep;
                if (_cfg.MinValue < _cfg.MaxValue && _currentValue < _cfg.MinValue)
                    _currentValue = _cfg.MaxValue;
                return _currentValue;

            case GenerationType.Sine:
            {
                double period = _cfg.SinePeriodSeconds > 0 ? _cfg.SinePeriodSeconds : 60;
                double intervalSec = _cfg.UpdateIntervalMs / 1000.0;
                _phase += 2 * Math.PI * intervalSec / period;
                if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
                double mid = (_cfg.MaxValue + _cfg.MinValue) / 2.0;
                double amp = (_cfg.MaxValue - _cfg.MinValue) / 2.0;
                return mid + amp * Math.Sin(_phase);
            }

            default:
                return _cfg.ConstantValue;
        }
    }

    private double ApplyScatterness(double value)
    {
        return _cfg.ScatternessType switch
        {
            ScatternessType.Percentage when _cfg.ScatternessValue > 0 =>
                value + value * (_rng.NextDouble() * 2 - 1) * (_cfg.ScatternessValue / 100.0),
            ScatternessType.Absolute when _cfg.ScatternessValue > 0 =>
                value + (_rng.NextDouble() * 2 - 1) * _cfg.ScatternessValue,
            _ => value
        };
    }

    /// <summary>Reset the generator state (e.g. after config update).</summary>
    public void Reset() { _currentValue = _cfg.InitialValue; _phase = 0; _tick = 0; }
}
