import gymnasium as gym
import gymize
import numpy as np
import os
from stable_baselines3 import PPO
from stable_baselines3.common.vec_env import DummyVecEnv
from stable_baselines3.common.callbacks import BaseCallback

# === Параметры Unity и Gymize ===
UNITY_ENV_NAME = "deer"  # Имя должно совпадать с Env Name в Unity Gym Manager
UNITY_BUILD_PATH = None  # None — если используете Editor, иначе путь к .exe/.x86_64
RENDER_MODE = None       # 'video' если нужен рендер, иначе None

# === Отладка ===
DEBUG_REWARDS = True     # Печатать информацию о наградах
DEBUG_ACTIONS = False    # Печатать действия агента

class RewardDebugCallback(BaseCallback):
    def __init__(self, verbose=0):
        super(RewardDebugCallback, self).__init__(verbose)
        self.episode_rewards = []
        self.episode_lengths = []
        
    def _on_step(self) -> bool:
        # Получаем информацию о наградах
        if 'episode' in self.locals.get('infos', [{}])[0]:
            episode_info = self.locals['infos'][0]['episode']
            episode_reward = episode_info['r']
            episode_length = episode_info['l']
            
            self.episode_rewards.append(episode_reward)
            self.episode_lengths.append(episode_length)
            
            # Печатаем статистику каждые 10 эпизодов
            if len(self.episode_rewards) % 10 == 0:
                avg_reward = np.mean(self.episode_rewards[-10:])
                avg_length = np.mean(self.episode_lengths[-10:])
                print(f"[DEBUG] Последние 10 эпизодов: средняя награда = {avg_reward:.2f}, средняя длина = {avg_length:.1f}")
                
        return True

# === Observation Space ===
# Формат Dict, см. CollectObservations в DeerAgentRL.cs
observation_space = gym.spaces.Dict({
    "obs_vision": gym.spaces.Box(low=-np.inf, high=np.inf, shape=(4, 5), dtype=np.float32),
    "head_yaw": gym.spaces.Box(low=-1.0, high=1.0, shape=(1,), dtype=np.float32),
    "head_pitch": gym.spaces.Box(low=-1.0, high=1.0, shape=(1,), dtype=np.float32),  # <-- добавлено
    "position": gym.spaces.Box(low=-1000, high=1000, shape=(2,), dtype=np.float32),
    "visited": gym.spaces.Box(low=0, high=10000, shape=(1,), dtype=np.float32),
    "food_in_memory": gym.spaces.Box(low=np.array([0, -1, -1]), high=np.array([1, 1000, 1]), shape=(3,), dtype=np.float32),
})

# === Action Space ===
action_space = gym.spaces.Box(
    low=np.array([-1, -1, 0, -1, -1], dtype=np.float32),
    high=np.array([1, 1, 1, 1, 1], dtype=np.float32),
    shape=(5,),
    dtype=np.float32
)

# === Создание среды ===
env = gym.make(
    'gymize/Unity-v0',
    env_name=UNITY_ENV_NAME,
    file_name=UNITY_BUILD_PATH,
    observation_space=observation_space,
    action_space=action_space,
    render_mode=RENDER_MODE,
)

# === Векторизация (SB3 требует VecEnv) ===
vec_env = DummyVecEnv([lambda: env])

# === Путь к единственной модели ===
MODEL_PATH = os.path.join("ppo_deeragentrl.zip")

# === Загрузка или создание новой PPO модели ===
if os.path.exists(MODEL_PATH):
    print(f"Загружаем существующую модель из {MODEL_PATH}")
    model = PPO.load(MODEL_PATH, env=vec_env)
else:
    print("Создаём новую PPO модель")
    model = PPO(
        "MultiInputPolicy",  # Для Dict observation
        vec_env,
        verbose=1,
        n_steps=2048,        # Уменьшено для более частых обновлений
        batch_size=256,      # Уменьшено для стабильности
        learning_rate=1e-3,  # УВЕЛИЧЕН для быстрого обучения
        max_grad_norm=1.0,   # Увеличен для более агрессивного обучения
        ent_coef=0.001,      # СИЛЬНО уменьшен - минимум хаоса, максимум целенаправленности
        gamma=0.9,           # ЕЩЕ больше фокус на ближайших наградах (еда здесь и сейчас!)
        gae_lambda=0.95,     # Немного увеличено
    )

total_timesteps = 200_000  # Можно увеличить для финального обучения

# Создаем callback для отладки
debug_callback = RewardDebugCallback() if DEBUG_REWARDS else None

print("=== НАЧАЛО ОБУЧЕНИЯ ===")
print(f"Целевые timesteps: {total_timesteps}")
print("ВАЖНО: Следите за логами Unity на предмет сообщений о еде!")

try:
    model.learn(
        total_timesteps=total_timesteps,
        progress_bar=True,
        callback=debug_callback
    )
except KeyboardInterrupt:
    print("\n=== Обнаружено прерывание (Ctrl+C), сохраняем модель... ===")
finally:
    model.save(MODEL_PATH)
    print(f"=== Модель сохранена в {MODEL_PATH} ===")

# === Пример инференса (опционально) ===
# obs, info = env.reset()
# for _ in range(1000):
#     action, _ = model.predict(obs, deterministic=True)
#     obs, reward, terminated, truncated, info = env.step(action)
#     if terminated or truncated:
#         obs, info = env.reset()
